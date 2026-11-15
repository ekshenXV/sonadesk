#pragma once

#include <pluginterfaces/base/ibstream.h>
#include <pluginterfaces/gui/iplugview.h>
#include <pluginterfaces/vst/ivsthostapplication.h>
#include <pluginterfaces/vst/ivstmessage.h>
#include <pluginterfaces/vst/ivstparameterchanges.h>
#include <algorithm>
#include <cstring>
#include <atomic>
#include <map>
#include <string>
#include <vector>

namespace mic {
using namespace Steinberg;
using namespace Steinberg::Vst;
constexpr int maxStateSize = 8 * 1024 * 1024;

inline bool sameId(const char* a, const char* b) { return std::memcmp(a, b, sizeof(TUID)) == 0; }

template<class Interface>
class HostObject : public Interface {
public:
    tresult PLUGIN_API queryInterface(const TUID id, void** object) override {
        if (!object) return kInvalidArgument;
        *object = nullptr;
        if (!sameId(id, FUnknown_iid) && !sameId(id, interfaceId())) return kNoInterface;
        *object = static_cast<Interface*>(this);
        addRef();
        return kResultOk;
    }
    uint32 PLUGIN_API addRef() override { return 1; }
    uint32 PLUGIN_API release() override { return 1; }
protected:
    virtual const char* interfaceId() const = 0;
};

template<class Target, class Source>
Target* query(Source* source, const TUID iid) {
    Target* value = nullptr;
    if (source) source->queryInterface(iid, reinterpret_cast<void**>(&value));
    return value;
}

template<class Target>
void release(Target*& object) {
    if (object) {
        object->release();
        object = nullptr;
    }
}

class HostApplication final : public HostObject<IHostApplication> {
    const char* interfaceId() const override { return IHostApplication_iid; }
public:
    tresult PLUGIN_API getName(String128 name) override {
        constexpr char16 text[] = u"Mic Effects";
        std::copy(text, text + sizeof(text) / sizeof(*text), name);
        return kResultOk;
    }
    tresult PLUGIN_API createInstance(TUID cid, TUID iid, void** object) override;
};

class Attributes final : public HostObject<IAttributeList> {
    const char* interfaceId() const override { return IAttributeList_iid; }
    std::atomic<uint32> references { 1 };
    std::map<std::string, int64> integers;
    std::map<std::string, double> numbers;
    std::map<std::string, std::u16string> strings;
    std::map<std::string, std::vector<char>> binary;
public:
    uint32 PLUGIN_API addRef() override { return ++references; }
    uint32 PLUGIN_API release() override {
        uint32 remaining = --references;
        if (remaining == 0) delete this;
        return remaining;
    }
    tresult PLUGIN_API setInt(AttrID id, int64 value) override { integers[id] = value; return kResultOk; }
    tresult PLUGIN_API getInt(AttrID id, int64& value) override {
        auto found = integers.find(id);
        if (found == integers.end()) return kResultFalse;
        value = found->second;
        return kResultOk;
    }
    tresult PLUGIN_API setFloat(AttrID id, double value) override { numbers[id] = value; return kResultOk; }
    tresult PLUGIN_API getFloat(AttrID id, double& value) override {
        auto found = numbers.find(id);
        if (found == numbers.end()) return kResultFalse;
        value = found->second;
        return kResultOk;
    }
    tresult PLUGIN_API setString(AttrID id, const TChar* value) override {
        if (!value) return kInvalidArgument;
        strings[id] = value;
        return kResultOk;
    }
    tresult PLUGIN_API getString(AttrID id, TChar* value, uint32 size) override {
        auto found = strings.find(id);
        if (found == strings.end() || !value || size < sizeof(TChar)) return kResultFalse;
        size_t length = std::min(found->second.size(), static_cast<size_t>(size / sizeof(TChar) - 1));
        std::copy_n(found->second.data(), length, value);
        value[length] = 0;
        return kResultOk;
    }
    tresult PLUGIN_API setBinary(AttrID id, const void* value, uint32 size) override {
        if ((!value && size) || size > maxStateSize) return kInvalidArgument;
        auto& bytes = binary[id];
        if (!size) bytes.clear();
        else bytes.assign(static_cast<const char*>(value), static_cast<const char*>(value) + size);
        return kResultOk;
    }
    tresult PLUGIN_API getBinary(AttrID id, const void*& value, uint32& size) override {
        auto found = binary.find(id);
        if (found == binary.end()) return kResultFalse;
        value = found->second.data();
        size = static_cast<uint32>(found->second.size());
        return kResultOk;
    }
};

class HostMessage final : public HostObject<IMessage> {
    const char* interfaceId() const override { return IMessage_iid; }
    std::atomic<uint32> references { 1 };
    std::string id;
    Attributes* attributes = new Attributes();
public:
    ~HostMessage() { attributes->release(); }
    uint32 PLUGIN_API addRef() override { return ++references; }
    uint32 PLUGIN_API release() override {
        uint32 remaining = --references;
        if (remaining == 0) delete this;
        return remaining;
    }
    FIDString PLUGIN_API getMessageID() override { return id.c_str(); }
    void PLUGIN_API setMessageID(FIDString value) override { id = value ? value : ""; }
    IAttributeList* PLUGIN_API getAttributes() override { return attributes; }
};

inline tresult HostApplication::createInstance(TUID cid, TUID iid, void** object) {
    if (!object) return kInvalidArgument;
    *object = nullptr;
    if (sameId(cid, IMessage_iid) && sameId(iid, IMessage_iid))
        *object = static_cast<IMessage*>(new HostMessage());
    else if (sameId(cid, IAttributeList_iid) && sameId(iid, IAttributeList_iid))
        *object = static_cast<IAttributeList*>(new Attributes());
    return *object ? kResultOk : kNoInterface;
}

class StateStream final : public HostObject<IBStream> {
    const char* interfaceId() const override { return IBStream_iid; }
    int64 offset = 0;
public:
    std::vector<char> bytes;
    StateStream() = default;
    StateStream(const char* data, int length) : bytes(data, data + length) {}
    tresult PLUGIN_API read(void* buffer, int32 count, int32* actual) override {
        if (actual) *actual = 0;
        if (!buffer || count < 0) return kInvalidArgument;
        int32 size = static_cast<int32>(std::min<int64>(count, std::max<int64>(0, static_cast<int64>(bytes.size()) - offset)));
        if (size != 0) std::memcpy(buffer, bytes.data() + offset, size);
        offset += size;
        if (actual) *actual = size;
        return kResultOk;
    }
    tresult PLUGIN_API write(void* buffer, int32 count, int32* actual) override {
        if (actual) *actual = 0;
        if (!buffer || count < 0 || count > maxStateSize - offset) return kInvalidArgument;
        if (count == 0) return kResultOk;
        if (offset + count > static_cast<int64>(bytes.size())) bytes.resize(offset + count);
        std::memcpy(bytes.data() + offset, buffer, count);
        offset += count;
        if (actual) *actual = count;
        return kResultOk;
    }
    tresult PLUGIN_API seek(int64 position, int32 mode, int64* actual) override {
        if (position < -maxStateSize || position > maxStateSize) return kInvalidArgument;
        if (mode == kIBSeekCur) position += offset;
        else if (mode == kIBSeekEnd) position += bytes.size();
        else if (mode != kIBSeekSet) return kInvalidArgument;
        if (position < 0 || position > maxStateSize) return kInvalidArgument;
        offset = position;
        if (actual) *actual = offset;
        return kResultOk;
    }
    tresult PLUGIN_API tell(int64* position) override {
        if (!position) return kInvalidArgument;
        *position = offset;
        return kResultOk;
    }
};

class ParameterQueue final : public HostObject<IParamValueQueue> {
    const char* interfaceId() const override { return IParamValueQueue_iid; }
public:
    ParamID id = 0;
    ParamValue value = 0;
    int32 offset = 0;
    int32 count = 0;

    ParamID PLUGIN_API getParameterId() override { return id; }
    int32 PLUGIN_API getPointCount() override { return count; }
    tresult PLUGIN_API getPoint(int32 index, int32& sample, ParamValue& result) override {
        if (index != 0 || count == 0) return kResultFalse;
        sample = offset;
        result = value;
        return kResultOk;
    }
    tresult PLUGIN_API addPoint(int32 sample, ParamValue next, int32& index) override {
        offset = sample;
        value = next;
        count = 1;
        index = 0;
        return kResultOk;
    }
};

class ParameterChanges final : public HostObject<IParameterChanges> {
    const char* interfaceId() const override { return IParameterChanges_iid; }
    std::vector<ParameterQueue> queues;
    int32 count = 0;
public:
    void reserve(int32 size) { queues.resize(std::max<int32>(64, size)); }
    void clear() { count = 0; }
    void swap(ParameterChanges& other) {
        queues.swap(other.queues);
        std::swap(count, other.count);
    }
    void set(ParamID id, ParamValue value) {
        int32 index;
        if (auto queue = addParameterData(id, index))
            queue->addPoint(0, value, index);
    }
    int32 PLUGIN_API getParameterCount() override { return count; }
    IParamValueQueue* PLUGIN_API getParameterData(int32 index) override {
        return index >= 0 && index < count ? &queues[index] : nullptr;
    }
    IParamValueQueue* PLUGIN_API addParameterData(const ParamID& id, int32& index) override {
        for (index = 0; index < count; ++index)
            if (queues[index].id == id) return &queues[index];
        if (count >= static_cast<int32>(queues.size())) return nullptr;
        auto& queue = queues[count++];
        queue.id = id;
        queue.count = 0;
        return &queue;
    }
};
}
