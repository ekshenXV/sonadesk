#include <windows.h>
#include <audioenginebaseapo.h>
#include <audioengineextensionapo.h>
#include <new>
#include <wchar.h>
#include <evntprov.h>
#include "Bridge.h"

namespace {
constexpr GUID classId{0x58e9874d, 0x7075, 0x4074, {0x9a, 0x8b, 0x44, 0xbd, 0x70, 0x83, 0x97, 0x69}};
constexpr GUID unknownId{0, 0, 0, {0xc0, 0, 0, 0, 0, 0, 0, 0x46}};
constexpr GUID factoryId{1, 0, 0, {0xc0, 0, 0, 0, 0, 0, 0, 0x46}};
constexpr GUID apoId{0xfd7f2b29, 0x24d0, 0x4b5c, {0xb1, 0x77, 0x59, 0x2c, 0x39, 0xf9, 0xca, 0x10}};
constexpr GUID rtId{0x9e1d6a6d, 0xddbc, 0x4e95, {0xa4, 0xc7, 0xad, 0x64, 0xba, 0x37, 0x84, 0x6c}};
constexpr GUID configurationId{0x0e5ed805, 0xaba6, 0x49c3, {0x8f, 0x9a, 0x2b, 0x8c, 0x88, 0x9c, 0x4f, 0xa8}};
constexpr GUID effectsId{0x5fa00f27, 0xadd6, 0x499a, {0x8a, 0x9d, 0x6b, 0x98, 0x52, 0x1f, 0xa7, 0x5b}};
constexpr GUID effects2Id{0xbafe99d2, 0x7436, 0x44ce, {0x9e, 0x0e, 0x4d, 0x89, 0xaf, 0xbf, 0xff, 0x56}};
constexpr GUID floatId{3, 0, 0x10, {0x80, 0, 0, 0xaa, 0, 0x38, 0x9b, 0x71}};
constexpr PROPERTYKEY endpointId{{0x1da5d803, 0xd492, 0x4edd, {0x8c, 0x23, 0xe0, 0xc0, 0xff, 0xee, 0x7f, 0x0e}}, 4};
constexpr APO_FLAG flags = static_cast<APO_FLAG>(APO_FLAG_DEFAULT | APO_FLAG_INPLACE);
volatile LONG objects = 0;
volatile LONG locks = 0;

// Emit lifecycle diagnostics only when a trace session enables this provider.
void trace(const wchar_t* message) {
    constexpr GUID provider{0xcba48dba, 0xe023, 0x4ba7, {0xae, 0x18, 0x25, 0x73, 0xf5, 0xdf, 0xf0, 0xa2}};
    REGHANDLE handle = 0;
    if (EventRegister(&provider, nullptr, nullptr, &handle) == ERROR_SUCCESS) {
        EventWriteString(handle, 4, 0, message);
        EventUnregister(handle);
    }
}

bool checkFormat(IAudioMediaType* media, UNCOMPRESSEDAUDIOFORMAT& format) {
    return media && SUCCEEDED(media->GetUncompressedAudioFormat(&format)) &&
        IsEqualGUID(format.guidFormatType, floatId) && format.dwBytesPerSampleContainer == sizeof(float) &&
        format.dwValidBitsPerSample == 32 && format.dwSamplesPerFrame > 0 && format.dwSamplesPerFrame <= 32 &&
        format.fFramesPerSecond >= 8000 && format.fFramesPerSecond <= 384000;
}

bool formatsMatch(const UNCOMPRESSEDAUDIOFORMAT& left, const UNCOMPRESSEDAUDIOFORMAT& right) {
    return left.dwSamplesPerFrame == right.dwSamplesPerFrame && left.fFramesPerSecond == right.fFramesPerSecond &&
        left.dwBytesPerSampleContainer == right.dwBytesPerSampleContainer;
}

class MicApo final : public IAudioProcessingObject, public IAudioProcessingObjectRT,
    public IAudioProcessingObjectConfiguration, public IAudioSystemEffects2 {
    class InnerUnknown final : public IUnknown {
        MicApo& instance_;
    public:
        explicit InnerUnknown(MicApo& instance) : instance_(instance) {}
        HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** result) override {
            return instance_.queryInner(iid, result);
        }
        ULONG STDMETHODCALLTYPE AddRef() override { return InterlockedIncrement(&instance_.refs_); }
        ULONG STDMETHODCALLTYPE Release() override {
            const LONG remaining = InterlockedDecrement(&instance_.refs_);
            if (!remaining) delete &instance_;
            return remaining;
        }
    };

    LONG refs_ = 1;
    InnerUnknown inner_;
    IUnknown* controller_;
    bool initialized_ = false;
    bool locked_ = false;
    UINT32 frameLimit_ = 0;
    UINT32 channelCount_ = 0;
    wchar_t bridgePath_[MAX_PATH]{};
    wchar_t mappingName_[100]{};
    wchar_t ownerSid_[190]{};
    mic::CaptureBridge bridge_;

    void reportStatus(DWORD status) const {
        if (!bridgePath_[0] || status == ERROR_LOCK_VIOLATION || status == ERROR_ALREADY_EXISTS)
            return;
        HANDLE marker = CreateFileW(bridgePath_, GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
            nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (marker == INVALID_HANDLE_VALUE)
            return;
        // The first byte is locked by the active APO. Leave it untouched.
        if (SetFilePointer(marker, 1, nullptr, FILE_BEGIN) != INVALID_SET_FILE_POINTER) {
            DWORD written = 0;
            WriteFile(marker, &status, sizeof(status), &written, nullptr);
        }
        CloseHandle(marker);
    }

    HRESULT negotiate(IAudioMediaType* opposite, IAudioMediaType* requested, IAudioMediaType** result) {
        if (!result) return E_POINTER;
        *result = nullptr;
        UNCOMPRESSEDAUDIOFORMAT want{}, other{};
        const bool validOpposite = checkFormat(opposite, other);
        if (checkFormat(requested, want) && (!opposite || (validOpposite && formatsMatch(want, other)))) {
            requested->AddRef();
            *result = requested;
            return S_OK;
        }
        if (validOpposite) {
            opposite->AddRef();
            *result = opposite;
            return S_FALSE;
        }
        return APOERR_FORMAT_NOT_SUPPORTED;
    }

    void locateBridge(IPropertyStore* properties, IMMDeviceCollection* devices) {
        GUID endpoint{};
        bool found = false;
        if (properties) {
            PROPVARIANT variant{};
            found = SUCCEEDED(properties->GetValue(endpointId, &variant)) && variant.vt == VT_LPWSTR
                && variant.pwszVal && SUCCEEDED(CLSIDFromString(variant.pwszVal, &endpoint));
            PropVariantClear(&variant);
        }
        if (!found && devices) {
            UINT count = 0;
            IMMDevice* device = nullptr;
            if (SUCCEEDED(devices->GetCount(&count)) && count &&
                SUCCEEDED(devices->Item(count - 1, &device))) {
                LPWSTR name = nullptr;
                if (SUCCEEDED(device->GetId(&name)) && name) {
                    const wchar_t* guid = wcsrchr(name, L'{');
                    found = guid && SUCCEEDED(CLSIDFromString(guid, &endpoint));
                }
                CoTaskMemFree(name);
                device->Release();
            }
        }
        if (!found) {
            trace(L"APO could not identify the capture endpoint");
            return;
        }
        wchar_t guid[39]{};
        StringFromGUID2(endpoint, guid, 39);
        wchar_t key[128]{};
        swprintf(key, 128, L"SOFTWARE\\MicEffects\\Endpoints\\%ls", guid);
        DWORD bytes = sizeof(bridgePath_);
        if (RegGetValueW(HKEY_LOCAL_MACHINE, key, L"BridgePath", RRF_RT_REG_SZ,
                nullptr, bridgePath_, &bytes) != ERROR_SUCCESS)
            bridgePath_[0] = L'\0';
        bytes = sizeof(ownerSid_);
        if (RegGetValueW(HKEY_LOCAL_MACHINE, key, L"Owner", RRF_RT_REG_SZ,
                nullptr, ownerSid_, &bytes) != ERROR_SUCCESS)
            bridgePath_[0] = L'\0';
        if (!bridgePath_[0])
            trace(L"APO could not read its endpoint settings");
        swprintf(mappingName_, 100, L"Global\\MicEffects-%ls", guid);
    }

public:
    explicit MicApo(IUnknown* outer) : inner_(*this), controller_(outer ? outer : &inner_) {
        InterlockedIncrement(&objects);
    }
    ~MicApo() { bridge_.close(); InterlockedDecrement(&objects); }

    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** result) override {
        return controller_->QueryInterface(iid, result);
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return controller_->AddRef(); }
    ULONG STDMETHODCALLTYPE Release() override { return controller_->Release(); }

    IUnknown& innerUnknown() { return inner_; }

    HRESULT queryInner(REFIID iid, void** result) {
        if (!result) return E_POINTER;
        *result = nullptr;
        if (IsEqualGUID(iid, unknownId)) {
            *result = static_cast<IUnknown*>(&inner_);
            inner_.AddRef();
            return S_OK;
        }
        if (IsEqualGUID(iid, apoId))
            *result = static_cast<IAudioProcessingObject*>(this);
        else if (IsEqualGUID(iid, rtId))
            *result = static_cast<IAudioProcessingObjectRT*>(this);
        else if (IsEqualGUID(iid, configurationId))
            *result = static_cast<IAudioProcessingObjectConfiguration*>(this);
        else if (IsEqualGUID(iid, effectsId))
            *result = static_cast<IAudioSystemEffects*>(this);
        else if (IsEqualGUID(iid, effects2Id))
            *result = static_cast<IAudioSystemEffects2*>(this);
        if (!*result) return E_NOINTERFACE;
        AddRef();
        return S_OK;
    }

    HRESULT STDMETHODCALLTYPE Reset() override { return locked_ ? APOERR_APO_LOCKED : S_OK; }
    HRESULT STDMETHODCALLTYPE GetLatency(HNSTIME* time) override {
        if (!time) return E_POINTER;
        *time = mic::delayMilliseconds * 10000;
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetEffectsList(LPGUID* effects, UINT* count, HANDLE) override {
        if (!effects || !count) return E_POINTER;
        *effects = static_cast<LPGUID>(CoTaskMemAlloc(sizeof(GUID)));
        *count = 0;
        if (!*effects) return E_OUTOFMEMORY;
        **effects = classId;
        *count = 1;
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetRegistrationProperties(APO_REG_PROPERTIES** result) override {
        if (!result) return E_POINTER;
        auto* registration = static_cast<APO_REG_PROPERTIES*>(CoTaskMemAlloc(sizeof(APO_REG_PROPERTIES)));
        *result = registration;
        if (!registration) return E_OUTOFMEMORY;
        *registration = APO_REG_PROPERTIES{};
        registration->clsid = classId;
        registration->Flags = flags;
        wcscpy(registration->szFriendlyName, L"Mic Effects");
        registration->u32MajorVersion = 1;
        registration->u32MinInputConnections = registration->u32MaxInputConnections = 1;
        registration->u32MinOutputConnections = registration->u32MaxOutputConnections = 1;
        registration->u32MaxInstances = UINT32_MAX;
        registration->u32NumAPOInterfaces = 1;
        registration->iidAPOInterfaceList[0] = apoId;
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE Initialize(UINT32 size, BYTE* data) override {
        trace(L"Initialize");
        if (initialized_) return APOERR_ALREADY_INITIALIZED;
        if (!data || size < sizeof(APOInitBaseStruct)) return E_INVALIDARG;
        auto* base = reinterpret_cast<APOInitBaseStruct*>(data);
        if (!IsEqualGUID(base->clsid, classId)) return APOERR_INVALID_APO_CLSID;
        if (size == sizeof(APOInitSystemEffects)) {
            auto* init = reinterpret_cast<APOInitSystemEffects*>(data);
            locateBridge(init->pAPOEndpointProperties, init->pDeviceCollection);
        } else if (size == sizeof(APOInitSystemEffects2)) {
            auto* init = reinterpret_cast<APOInitSystemEffects2*>(data);
            if (!init->InitializeForDiscoveryOnly) locateBridge(init->pAPOEndpointProperties, init->pDeviceCollection);
        } else if (size == sizeof(APOInitSystemEffects3)) {
            auto* init = reinterpret_cast<APOInitSystemEffects3*>(data);
            if (!init->InitializeForDiscoveryOnly) locateBridge(init->pAPOEndpointProperties, init->pDeviceCollection);
        }
        initialized_ = true;
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE IsInputFormatSupported(IAudioMediaType* opposite, IAudioMediaType* requested,
        IAudioMediaType** supported) override {
        const HRESULT result = negotiate(opposite, requested, supported);
        UNCOMPRESSEDAUDIOFORMAT format{};
        if (!locked_ && SUCCEEDED(result) && checkFormat(*supported, format))
            channelCount_ = format.dwSamplesPerFrame;
        return result;
    }
    HRESULT STDMETHODCALLTYPE IsOutputFormatSupported(IAudioMediaType* opposite, IAudioMediaType* requested,
        IAudioMediaType** supported) override { return negotiate(opposite, requested, supported); }
    HRESULT STDMETHODCALLTYPE GetInputChannelCount(UINT32* count) override {
        if (!count) return E_POINTER;
        *count = channelCount_;
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE LockForProcess(UINT32 inputs, APO_CONNECTION_DESCRIPTOR** input,
        UINT32 outputs, APO_CONNECTION_DESCRIPTOR** output) override {
        trace(L"LockForProcess");
        if (!initialized_) return APOERR_NOT_INITIALIZED;
        if (locked_) return APOERR_APO_LOCKED;
        if (inputs != 1 || outputs != 1) return APOERR_NUM_CONNECTIONS_INVALID;
        if (!input || !output || !input[0] || !output[0]) return E_POINTER;
        UNCOMPRESSEDAUDIOFORMAT in{}, out{};
        if (!checkFormat(input[0]->pFormat, in) || !checkFormat(output[0]->pFormat, out) || !formatsMatch(in, out)) {
            reportStatus(static_cast<DWORD>(APOERR_INVALID_CONNECTION_FORMAT));
            return APOERR_INVALID_CONNECTION_FORMAT;
        }
        if (input[0]->u32MaxFrameCount > output[0]->u32MaxFrameCount) {
            reportStatus(static_cast<DWORD>(APOERR_INVALID_OUTPUT_MAXFRAMECOUNT));
            return APOERR_INVALID_OUTPUT_MAXFRAMECOUNT;
        }
        channelCount_ = in.dwSamplesPerFrame;
        frameLimit_ = input[0]->u32MaxFrameCount;
        const DWORD bridgeError = bridge_.open(
            bridgePath_, mappingName_, ownerSid_, static_cast<UINT32>(in.fFramesPerSecond), channelCount_);
        wchar_t diagnostic[64]{};
        swprintf(diagnostic, 64, L"Open bridge: %lu", static_cast<unsigned long>(bridgeError));
        trace(diagnostic);
        reportStatus(bridgeError);
        locked_ = true;
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE UnlockForProcess() override {
        if (!locked_) return APOERR_ALREADY_UNLOCKED;
        bridge_.close();
        locked_ = false;
        return S_OK;
    }
    UINT32 STDMETHODCALLTYPE CalcInputFrames(UINT32 count) override { return count; }
    UINT32 STDMETHODCALLTYPE CalcOutputFrames(UINT32 count) override { return count; }
    void STDMETHODCALLTYPE APOProcess(UINT32 inputs, APO_CONNECTION_PROPERTY** input,
        UINT32 outputs, APO_CONNECTION_PROPERTY** output) override {
        if (!locked_ || inputs != 1 || outputs != 1 || !input || !output || !input[0] || !output[0]) return;
        auto& source = *input[0];
        auto& target = *output[0];
        const UINT32 frames = source.u32ValidFrameCount;
        if (frames > frameLimit_ || source.u32BufferFlags == BUFFER_INVALID) {
            target.u32BufferFlags = BUFFER_INVALID;
            target.u32ValidFrameCount = 0;
            return;
        }
        auto* from = reinterpret_cast<const float*>(source.pBuffer);
        auto* to = reinterpret_cast<float*>(target.pBuffer);
        const bool silent = source.u32BufferFlags == BUFFER_SILENT;
        if (!to || (!silent && !from)) {
            target.u32BufferFlags = BUFFER_INVALID;
            target.u32ValidFrameCount = 0;
            return;
        }
        const bool processed = bridge_.process(from, to, frames, channelCount_, silent);
        if (!processed && !silent && from != to)
            memmove(to, from, static_cast<size_t>(frames) * channelCount_ * sizeof(float));
        target.u32BufferFlags = processed ? BUFFER_VALID : source.u32BufferFlags;
        target.u32ValidFrameCount = frames;
    }
};

class Factory final : public IClassFactory {
    LONG refs_ = 1;
public:
    Factory() { InterlockedIncrement(&objects); }
    ~Factory() { InterlockedDecrement(&objects); }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** result) override {
        if (!result) return E_POINTER;
        *result = nullptr;
        if (!IsEqualGUID(iid, unknownId) && !IsEqualGUID(iid, factoryId)) return E_NOINTERFACE;
        *result = static_cast<IClassFactory*>(this);
        AddRef();
        return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return InterlockedIncrement(&refs_); }
    ULONG STDMETHODCALLTYPE Release() override {
        const auto next = InterlockedDecrement(&refs_);
        if (!next) delete this;
        return next;
    }
    HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer, REFIID iid, void** result) override {
        if (!result) return E_POINTER;
        *result = nullptr;
        trace(outer ? L"CreateInstance with outer IUnknown" : L"CreateInstance");
        if (outer && !IsEqualGUID(unknownId, iid)) return E_NOINTERFACE;
        auto* apo = new(std::nothrow) MicApo(outer);
        if (!apo) return E_OUTOFMEMORY;
        IUnknown& inner = apo->innerUnknown();
        const HRESULT status = inner.QueryInterface(iid, result);
        inner.Release();
        return status;
    }
    HRESULT STDMETHODCALLTYPE LockServer(BOOL lock) override {
        if (lock) InterlockedIncrement(&locks);
        else InterlockedDecrement(&locks);
        return S_OK;
    }
};
}

extern "C" HRESULT WINAPI DllGetClassObject(REFCLSID clsid, REFIID iid, void** result) {
    trace(L"GetClassObject");
    if (!result) return E_POINTER;
    *result = nullptr;
    if (!IsEqualGUID(clsid, classId)) return CLASS_E_CLASSNOTAVAILABLE;
    auto* factory = new(std::nothrow) Factory();
    if (!factory) return E_OUTOFMEMORY;
    const auto status = factory->QueryInterface(iid, result);
    factory->Release();
    return status;
}

extern "C" HRESULT WINAPI DllCanUnloadNow() {
    return mic::load(objects) == 0 && mic::load(locks) == 0 ? S_OK : S_FALSE;
}
