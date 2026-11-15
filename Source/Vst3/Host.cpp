#include <windows.h>
#include "HostInterfaces.h"
#include <pluginterfaces/base/ipluginbase.h>
#include <pluginterfaces/vst/ivstaudioprocessor.h>
#include <pluginterfaces/vst/ivsteditcontroller.h>
#include <pluginterfaces/vst/ivstmessage.h>
#include <pluginterfaces/vst/ivstprocesscontext.h>
#include <cmath>
#include <mutex>

namespace mic {
constexpr int blockSize = 512;
constexpr int maxBuses = 32;
constexpr UINT resizeEditor = WM_APP + 45;
static HostApplication application;

struct Module {
    using Entry = bool (PLUGIN_API*)();
    HMODULE handle = nullptr;
    Entry exit = nullptr;
    IPluginFactory* factory = nullptr;
    int users = 1;

    explicit Module(HMODULE handle) : handle(handle) {}
    ~Module() {
        release(factory);
        if (exit) exit();
        FreeLibrary(handle);
    }
};

static std::vector<Module*> modules;

static Module* loadModule(const wchar_t* path) {
    HMODULE handle = LoadLibraryExW(path, nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
    if (!handle) return nullptr;
    for (auto* module : modules) {
        if (module->handle == handle) {
            ++module->users;
            FreeLibrary(handle);
            return module;
        }
    }
    auto* module = new Module(handle);
    auto entry = reinterpret_cast<Module::Entry>(GetProcAddress(handle, "InitDll"));
    if (entry && !entry()) {
        delete module;
        return nullptr;
    }
    module->exit = reinterpret_cast<Module::Entry>(GetProcAddress(handle, "ExitDll"));
    using FactoryEntry = IPluginFactory* (PLUGIN_API*)();
    auto factory = reinterpret_cast<FactoryEntry>(GetProcAddress(handle, "GetPluginFactory"));
    if (factory) module->factory = factory();
    if (!module->factory) {
        delete module;
        return nullptr;
    }
    if (auto* factory3 = query<IPluginFactory3>(module->factory, IPluginFactory3_iid)) {
        factory3->setHostContext(&application);
        factory3->release();
    }
    modules.push_back(module);
    return module;
}

static void unloadModule(Module* module) {
    if (!module || --module->users != 0) return;
    modules.erase(std::find(modules.begin(), modules.end(), module));
    delete module;
}

struct Buses {
    AudioBusBuffers buffers[maxBuses];
    float* pointers[maxBuses][64] {};
    float samples[2][blockSize] {};
    int32 count = 0;
    int32 main = -1;

    bool scan(IComponent* component, BusDirection direction) {
        count = component->getBusCount(kAudio, direction);
        if (count < 1 || count > maxBuses) return false;
        main = -1;
        for (int32 index = 0; index < count; ++index) {
            BusInfo info {};
            if (component->getBusInfo(kAudio, direction, index, info) != kResultOk ||
                info.channelCount < 0 || info.channelCount > 64) return false;
            buffers[index].numChannels = info.channelCount;
            buffers[index].channelBuffers32 = pointers[index];
            if (main < 0 && info.busType == kMain && info.channelCount > 0) main = index;
        }
        return main >= 0;
    }

    bool configure(IComponent* component, BusDirection direction) {
        if (!scan(component, direction)) return false;
        int channels = buffers[main].numChannels;
        if (channels > 2) return false;
        for (int32 index = 0; index < count; ++index) {
            std::fill_n(pointers[index], 64, nullptr);
            if (index == main) {
                for (int channel = 0; channel < channels; ++channel) pointers[index][channel] = samples[channel];
                if (component->activateBus(kAudio, direction, index, true) != kResultOk) return false;
            } else {
                component->activateBus(kAudio, direction, index, false);
            }
        }
        return true;
    }
};

class Effect;

class ComponentHandler final : public HostObject<IComponentHandler> {
    const char* interfaceId() const override { return IComponentHandler_iid; }
    Effect& effect;
public:
    explicit ComponentHandler(Effect& effect) : effect(effect) {}
    tresult PLUGIN_API beginEdit(ParamID) override { return kResultOk; }
    tresult PLUGIN_API endEdit(ParamID) override { return kResultOk; }
    tresult PLUGIN_API performEdit(ParamID id, ParamValue value) override;
    tresult PLUGIN_API restartComponent(int32 flags) override;
};

class PlugFrame final : public HostObject<IPlugFrame> {
    const char* interfaceId() const override { return IPlugFrame_iid; }
public:
    HWND parent = nullptr;
    tresult PLUGIN_API resizeView(IPlugView* view, ViewRect* size) override {
        if (!parent || !view || !size || size->getWidth() < 1 || size->getHeight() < 1 ||
            size->getWidth() > 8192 || size->getHeight() > 8192) return kInvalidArgument;
        PostMessageW(parent, resizeEditor, size->getWidth(), size->getHeight());
        return view->onSize(size);
    }
};

class Effect {
public:
    Module* module = nullptr;
    IComponent* component = nullptr;
    IAudioProcessor* processor = nullptr;
    IEditController* controller = nullptr;
    IConnectionPoint* componentConnection = nullptr;
    IConnectionPoint* controllerConnection = nullptr;
    IPlugView* view = nullptr;
    ComponentHandler handler { *this };
    PlugFrame frame;
    bool componentInitialized = false;
    bool separateController = false;
    bool controllerInitialized = false;
    bool active = false;
    bool running = false;
    bool attached = false;
    int rate = 0;
    TUID classId {};
    Buses inputs;
    Buses outputs;
    ParameterChanges pending;
    ParameterChanges blockParameters;
    std::mutex parameterMutex;
    ProcessContext context {};
    std::vector<char> saved;
    std::vector<char> savedController;

    ~Effect() {
        closeEditor();
        stop();
        if (componentConnection && controllerConnection) {
            componentConnection->disconnect(controllerConnection);
            controllerConnection->disconnect(componentConnection);
        }
        release(componentConnection);
        release(controllerConnection);
        if (controller && (controllerInitialized || !separateController)) controller->setComponentHandler(nullptr);
        if (controllerInitialized) controller->terminate();
        release(controller);
        release(processor);
        if (componentInitialized) component->terminate();
        release(component);
        unloadModule(module);
    }

    bool hasUsableBuses() { return inputs.scan(component, kInput) && outputs.scan(component, kOutput); }

    bool load(Module* loaded, const char* requested) {
        module = loaded;
        auto* factory = module->factory;
        auto* factory2 = query<IPluginFactory2>(factory, IPluginFactory2_iid);
        const int32 count = factory->countClasses();
        for (int32 index = 0; index < count; ++index) {
            PClassInfo info {};
            if (factory->getClassInfo(index, &info) != kResultOk ||
                std::strcmp(info.category, kVstAudioEffectClass) != 0 ||
                (requested && !sameId(requested, info.cid))) continue;
            PClassInfo2 details {};
            if (factory2 && factory2->getClassInfo2(index, &details) == kResultOk &&
                std::strstr(details.subCategories, "Instrument")) continue;
            if (factory->createInstance(info.cid, IComponent_iid, reinterpret_cast<void**>(&component)) != kResultOk || !component) {
                release(component);
                continue;
            }
            component->setIoMode(kSimple);
            componentInitialized = component->initialize(&application) == kResultOk;
            if (componentInitialized)
                processor = query<IAudioProcessor>(component, IAudioProcessor_iid);
            if (processor && processor->canProcessSampleSize(kSample32) == kResultOk && hasUsableBuses()) {
                std::memcpy(classId, info.cid, sizeof(classId));
                break;
            }
            release(processor);
            if (componentInitialized) component->terminate();
            componentInitialized = false;
            release(component);
        }
        release(factory2);
        if (!processor) return false;

        controller = query<IEditController>(component, IEditController_iid);
        if (!controller) {
            TUID controllerId {};
            if (component->getControllerClassId(controllerId) == kResultOk &&
                factory->createInstance(controllerId, IEditController_iid, reinterpret_cast<void**>(&controller)) == kResultOk && controller) {
                separateController = true;
                controllerInitialized = controller->initialize(&application) == kResultOk;
                if (!controllerInitialized) release(controller);
            } else {
                release(controller);
            }
        }
        const int32 parameters = controller ? std::clamp(controller->getParameterCount(), 0, 65536) : 0;
        pending.reserve(parameters);
        blockParameters.reserve(parameters);
        if (controller) {
            controller->setComponentHandler(&handler);
            if (separateController) {
                componentConnection = query<IConnectionPoint>(component, IConnectionPoint_iid);
                controllerConnection = query<IConnectionPoint>(controller, IConnectionPoint_iid);
                if (componentConnection && controllerConnection) {
                    componentConnection->connect(controllerConnection);
                    controllerConnection->connect(componentConnection);
                }
            }
        }
        return true;
    }

    bool restore(const char* data, int length, const char* controllerData, int controllerLength) {
        if (length > maxStateSize || controllerLength > maxStateSize) return false;
        if (length > 0 && component) {
            StateStream state(data, length);
            if (component->setState(&state) != kResultOk) return false;
            if (controller) {
                StateStream componentState(data, length);
                controller->setComponentState(&componentState);
            }
        } else if (controller) {
            StateStream defaults;
            if (component->getState(&defaults) == kResultOk) {
                defaults.seek(0, IBStream::kIBSeekSet, nullptr);
                controller->setComponentState(&defaults);
            }
        }
        if (controllerLength > 0 && controller) {
            StateStream state(controllerData, controllerLength);
            controller->setState(&state);
        }
        return true;
    }

    void queueParameters() {
        if (!controller) return;
        const int32 count = std::clamp(controller->getParameterCount(), 0, 65536);
        for (int32 index = 0; index < count; ++index) {
            ParameterInfo info {};
            if (controller->getParameterInfo(index, info) == kResultOk && !(info.flags & ParameterInfo::kIsReadOnly))
                handler.performEdit(info.id, controller->getParamNormalized(info.id));
        }
    }

    bool start(int sampleRate) {
        if (running && rate == sampleRate) return true;
        stop();
        SpeakerArrangement ins[maxBuses] {};
        SpeakerArrangement outs[maxBuses] {};
        for (int32 i = 0; i < inputs.count; ++i) processor->getBusArrangement(kInput, i, ins[i]);
        for (int32 i = 0; i < outputs.count; ++i) processor->getBusArrangement(kOutput, i, outs[i]);
        ins[inputs.main] = inputs.buffers[inputs.main].numChannels == 1 ? SpeakerArr::kMono : SpeakerArr::kStereo;
        outs[outputs.main] = outputs.buffers[outputs.main].numChannels == 1 ? SpeakerArr::kMono : SpeakerArr::kStereo;
        processor->setBusArrangements(ins, inputs.count, outs, outputs.count);
        if (!inputs.configure(component, kInput) || !outputs.configure(component, kOutput)) return false;
        ProcessSetup setup { kRealtime, kSample32, blockSize, static_cast<double>(sampleRate) };
        if (processor->setupProcessing(setup) != kResultOk || component->setActive(true) != kResultOk) return false;
        active = true;
        const tresult result = processor->setProcessing(true);
        running = result == kResultOk || result == kNotImplemented;
        if (!running) {
            stop();
            return false;
        }
        rate = sampleRate;
        context = {};
        context.sampleRate = rate;
        context.state = ProcessContext::kContTimeValid;
        queueParameters();
        return true;
    }

    void stop() {
        if (running) processor->setProcessing(false);
        running = false;
        if (active) component->setActive(false);
        active = false;
    }

    bool process(const float* in, int channels, float* out, int frames) {
        if (!running || frames < 1 || frames > blockSize || channels < 1 || channels > 2) return false;
        const int32 inChannels = inputs.buffers[inputs.main].numChannels;
        for (int i = 0; i < frames; ++i) {
            const float left = in[i * channels];
            const float right = in[i * channels + channels - 1];
            inputs.samples[0][i] = inChannels == 1 ? (left + right) * .5f : left;
            inputs.samples[1][i] = right;
        }
        std::fill_n(outputs.samples[0], frames, 0.0f);
        std::fill_n(outputs.samples[1], frames, 0.0f);
        outputs.buffers[outputs.main].silenceFlags = 0;
        ProcessData data;
        data.processMode = kRealtime;
        data.numSamples = frames;
        data.numInputs = inputs.count;
        data.numOutputs = outputs.count;
        data.inputs = inputs.buffers;
        data.outputs = outputs.buffers;
        data.processContext = &context;
        {
            std::unique_lock<std::mutex> lock(parameterMutex, std::try_to_lock);
            if (lock.owns_lock()) pending.swap(blockParameters);
        }
        data.inputParameterChanges = &blockParameters;
        const tresult result = processor->process(data);
        blockParameters.clear();
        context.projectTimeSamples += frames;
        context.continousTimeSamples += frames;
        if (result != kResultOk) return false;

        const auto channelsOut = outputs.buffers[outputs.main].numChannels;
        const auto silent = outputs.buffers[outputs.main].silenceFlags;
        for (int i = 0; i < frames; ++i) {
            for (int channel = 0; channel < 2; ++channel) {
                const int source = std::min<int>(channel, channelsOut - 1);
                const float value = outputs.samples[source][i];
                out[i * 2 + channel] = (silent & (1ULL << source)) || !std::isfinite(value) ? 0 : value;
            }
        }
        return true;
    }

    bool makeView() {
        if (view) return true;
        if (!controller) return false;
        view = controller->createView(ViewType::kEditor);
        if (view && view->isPlatformTypeSupported(kPlatformTypeHWND) != kResultOk) release(view);
        return view != nullptr;
    }

    void closeEditor() {
        if (!view) return;
        if (attached) view->removed();
        attached = false;
        view->setFrame(nullptr);
        frame.parent = nullptr;
        release(view);
    }
};

tresult ComponentHandler::performEdit(ParamID id, ParamValue value) {
    std::lock_guard<std::mutex> lock(effect.parameterMutex);
    effect.pending.set(id, value);
    return kResultOk;
}

tresult ComponentHandler::restartComponent(int32 flags) {
    if (flags & kParamValuesChanged) effect.queueParameters();
    return (flags & (kReloadComponent | kIoChanged)) ? kResultFalse : kResultOk;
}
}

using namespace mic;
#define EXPORT extern "C" __declspec(dllexport)
EXPORT Effect* vst3_open(const wchar_t* path, const char* requested, char* actual) {
    Module* module = loadModule(path);
    if (!module) return nullptr;
    auto* effect = new Effect();
    if (!effect->load(module, requested)) {
        delete effect;
        return nullptr;
    }
    std::memcpy(actual, effect->classId, sizeof(TUID));
    return effect;
}
EXPORT void vst3_close(Effect* effect) { delete effect; }
EXPORT int vst3_start(Effect* effect, int rate) { return effect->start(rate); }
EXPORT void vst3_stop(Effect* effect) { effect->stop(); }
EXPORT int vst3_process(Effect* effect, const float* input, int channels, float* output, int frames) {
    return effect->process(input, channels, output, frames);
}
EXPORT int vst3_has_editor(Effect* effect) { return effect->makeView(); }
EXPORT int vst3_editor_size(Effect* effect, int* width, int* height) {
    ViewRect size {};
    if (!effect->makeView() || effect->view->getSize(&size) != kResultOk ||
        size.getWidth() < 1 || size.getHeight() < 1 || size.getWidth() > 8192 || size.getHeight() > 8192) return 0;
    *width = size.getWidth();
    *height = size.getHeight();
    return 1;
}
EXPORT int vst3_attach(Effect* effect, HWND parent) {
    if (!effect->makeView()) return 0;
    effect->frame.parent = parent;
    effect->view->setFrame(&effect->frame);
    effect->attached = effect->view->attached(parent, kPlatformTypeHWND) == kResultOk;
    if (!effect->attached) effect->closeEditor();
    return effect->attached;
}
EXPORT void vst3_remove(Effect* effect) { effect->closeEditor(); }
EXPORT int vst3_restore(Effect* effect, const char* data, int length, const char* controller, int controllerLength) {
    return effect->restore(data, length, controller, controllerLength);
}
EXPORT void vst3_save(Effect* effect, int* componentBytes, int* controllerBytes) {
    StateStream component;
    StateStream controller;
    effect->saved.clear();
    effect->savedController.clear();
    if (effect->component->getState(&component) == kResultOk) effect->saved.swap(component.bytes);
    if (effect->controller && effect->controller->getState(&controller) == kResultOk)
        effect->savedController.swap(controller.bytes);
    *componentBytes = static_cast<int>(effect->saved.size());
    *controllerBytes = static_cast<int>(effect->savedController.size());
}
EXPORT void vst3_copy_state(Effect* effect, char* component, char* controller) {
    if (component && !effect->saved.empty()) std::memcpy(component, effect->saved.data(), effect->saved.size());
    if (controller && !effect->savedController.empty())
        std::memcpy(controller, effect->savedController.data(), effect->savedController.size());
    effect->saved.clear();
    effect->savedController.clear();
}
