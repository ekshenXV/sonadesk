#pragma once
#include <windows.h>
#include <sddl.h>
#include <stdint.h>
#include <wchar.h>

namespace mic {
constexpr DWORD bridgeSize = 64 + 2 * 16384 * 2 * sizeof(float);
constexpr uint32_t ringFrames = 16384;
constexpr LONG protocolVersion = 2;
constexpr uint32_t delayMilliseconds = 35;

// Keep the layout in sync with AudioBridge.cs. Cursors count stereo frames.
struct Bridge {
    volatile LONG version;
    volatile LONG sampleRate;
    volatile LONG channels;
    volatile LONG inputEnd;
    volatile LONG outputEnd;
    volatile LONG enabled;
    volatile LONG outputStart;
    volatile LONG workerSession;
    volatile LONG outputSession;
    volatile LONG wetPosition;
    volatile LONG capturedFrames;
    LONG reserved[5];
    float input[ringFrames][2];
    float output[ringFrames][2];
};
static_assert(sizeof(Bridge) == bridgeSize, "Unexpected bridge layout");

inline LONG load(volatile LONG& value) { return InterlockedCompareExchange(&value, 0, 0); }
inline void store(volatile LONG& value, LONG next) { InterlockedExchange(&value, next); }
inline bool reached(uint32_t position, uint32_t target) {
    return static_cast<int32_t>(position - target) >= 0;
}

class CaptureBridge {
    HANDLE file_ = INVALID_HANDLE_VALUE;
    HANDLE mapping_ = nullptr;
    Bridge* shared_ = nullptr;
    LONG session_ = 0;
    uint32_t position_ = 0;
    uint32_t delay_ = 0;
    bool primed_ = false;
    bool activeOutput_ = false;
    float mix_ = 0.f;

public:
    ~CaptureBridge() { close(); }

    DWORD open(const wchar_t* path, const wchar_t* mappingName, const wchar_t* ownerSid,
        UINT32 sampleRate, UINT32 channels) {
        close();
        if (!path[0])
            return ERROR_PATH_NOT_FOUND;
        if (channels == 0 || channels > 2 || sampleRate < 8000 || sampleRate > 192000)
            return ERROR_NOT_SUPPORTED;
        file_ = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
            nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file_ == INVALID_HANDLE_VALUE)
            return GetLastError();
        OVERLAPPED owner{};
        // A file lock serializes APO instances. Audio lives in a pagefile-backed mapping.
        if (!LockFileEx(file_, LOCKFILE_EXCLUSIVE_LOCK | LOCKFILE_FAIL_IMMEDIATELY, 0, 1, 0, &owner)) {
            const DWORD error = GetLastError();
            close();
            return error;
        }
        wchar_t acl[240]{};
        swprintf(acl, 240, L"D:P(A;;GA;;;SY)(A;;GA;;;LS)(A;;GR;;;BA)(A;;GA;;;%ls)", ownerSid);
        PSECURITY_DESCRIPTOR descriptor = nullptr;
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(acl, SDDL_REVISION_1, &descriptor, nullptr)) {
            const DWORD error = GetLastError();
            close();
            return error;
        }
        SECURITY_ATTRIBUTES attributes{sizeof(SECURITY_ATTRIBUTES), descriptor, false};
        mapping_ = CreateFileMappingW(INVALID_HANDLE_VALUE, &attributes, PAGE_READWRITE, 0, bridgeSize, mappingName);
        const DWORD mappingStatus = GetLastError();
        LocalFree(descriptor);
        if (mapping_ && mappingStatus != ERROR_ALREADY_EXISTS)
            shared_ = static_cast<Bridge*>(MapViewOfFile(mapping_, FILE_MAP_WRITE, 0, 0, bridgeSize));
        if (!shared_) {
            const DWORD error = mapping_ && mappingStatus != ERROR_ALREADY_EXISTS ? GetLastError() : mappingStatus;
            close();
            return error;
        }
        store(shared_->version, 0);
        store(shared_->inputEnd, 0);
        store(shared_->outputEnd, 0);
        store(shared_->outputSession, 0);
        store(shared_->enabled, 0);
        store(shared_->sampleRate, static_cast<LONG>(sampleRate));
        store(shared_->channels, static_cast<LONG>(channels));
        position_ = 0;
        session_ = 0;
        delay_ = sampleRate * delayMilliseconds / 1000;
        primed_ = false;
        mix_ = 0.f;
        activeOutput_ = false;
        store(shared_->version, protocolVersion);
        return ERROR_SUCCESS;
    }

    void close() {
        if (shared_) {
            store(shared_->version, 0);
            UnmapViewOfFile(shared_);
            shared_ = nullptr;
        }
        if (mapping_) {
            CloseHandle(mapping_);
            mapping_ = nullptr;
        }
        if (file_ != INVALID_HANDLE_VALUE) {
            CloseHandle(file_);
            file_ = INVALID_HANDLE_VALUE;
        }
    }

    bool process(const float* input, float* output, UINT32 frames, UINT32 channels, bool silent) {
        if (!shared_ || frames == 0 || frames > ringFrames / 4)
            return false;

        const auto captured = static_cast<uint32_t>(load(shared_->capturedFrames));
        store(shared_->capturedFrames, static_cast<LONG>(captured + frames));
        const bool enabled = load(shared_->enabled) != 0;
        if (!enabled) {
            position_ = 0;
            store(shared_->inputEnd, 0);
            activeOutput_ = false;
            primed_ = false;
            mix_ = 0.f;
            store(shared_->wetPosition, 0);
            return false;
        }
        for (uint32_t frame = 0; frame < frames; ++frame) {
            auto& destination = shared_->input[(position_ + frame) & (ringFrames - 1)];
            const uint32_t sample = frame * channels;
            destination[0] = silent ? 0 : input[sample];
            destination[1] = silent ? 0 : input[sample + channels - 1];
        }
        store(shared_->inputEnd, static_cast<LONG>(position_ + frames));

        const LONG session = load(shared_->workerSession);
        if (session != session_) {
            session_ = session;
            mix_ = 0.f;
            activeOutput_ = false;
        }
        const uint32_t target = position_ - delay_;
        const uint32_t end = static_cast<uint32_t>(load(shared_->outputEnd));
        const bool outputReady = session != 0 && load(shared_->outputSession) == session;
        const bool available = primed_ && enabled && outputReady &&
            reached(target, static_cast<uint32_t>(load(shared_->outputStart))) &&
            reached(end, target + frames) && end - target < ringFrames;
        // Keep the dry signal in sync with the processed signal across a missed block.
        const bool delayed = primed_ && enabled &&
            (available || (activeOutput_ && outputReady &&
                reached(position_ + frames, end) && position_ + frames - end < 2 * delay_));
        activeOutput_ = delayed;
        if (delayed) {
            for (uint32_t frame = 0; frame < frames; ++frame) {
                auto& dry = shared_->input[(target + frame) & (ringFrames - 1)];
                auto& wet = shared_->output[(target + frame) & (ringFrames - 1)];
                mix_ = available ? (mix_ < 0.98f ? mix_ + 0.02f : 1.f) : 0.f;
                for (uint32_t channel = 0; channel < channels; ++channel) {
                    // The wet ring may be incomplete on underrun, so use only the dry sample.
                    output[frame * channels + channel] = available
                        ? dry[channel] + mix_ * (wet[channel] - dry[channel]) : dry[channel];
                }
            }
        } else {
            mix_ = 0.f;
        }
        position_ += frames;
        primed_ |= position_ >= delay_;
        store(shared_->wetPosition, available ? static_cast<LONG>(target + frames) : 0);
        return delayed;
    }
};
}
