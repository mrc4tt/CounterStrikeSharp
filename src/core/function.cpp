/**
 * =============================================================================
 * Source Python
 * Copyright (C) 2012-2015 Source Python Development Team.  All rights reserved.
 * =============================================================================
 *
 * This program is free software; you can redistribute it and/or modify it under
 * the terms of the GNU General Public License, version 3.0, as published by the
 * Free Software Foundation.
 *
 * This program is distributed in the hope that it will be useful, but WITHOUT
 * ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS
 * FOR A PARTICULAR PURPOSE.  See the GNU General Public License for more
 * details.
 *
 * You should have received a copy of the GNU General Public License along with
 * this program.  If not, see <http://www.gnu.org/licenses/>.
 *
 * As a special exception, the Source Python Team gives you permission
 * to link the code of this program (as well as its derivative works) to
 * "Half-Life 2," the "Source Engine," and any Game MODs that run on software
 * by the Valve Corporation.  You must obey the GNU General Public License in
 * all respects for all other code used.  Additionally, the Source.Python
 * Development Team grants this exception to all derivative works.
 *
 * This file has been modified from its original form, under the terms of GNU
 * General Public License, version 3.0.
 */

#include "core/function.h"

#include <algorithm>
#include <array>
#include <string_view>

#include "core/log.h"
#include "core/memory_module.h"
#include "dyncall/dyncall/dyncall.h"

#include <cassert>
#include <thread>

namespace counterstrikesharp {

// ============================================================================
// >> GetDynCallConvention
// ============================================================================
int GetDynCallConvention(Convention_t eConv)
{
    switch (eConv)
    {
        case CONV_CUSTOM:
            return -1;
        case CONV_CDECL:
            return DC_CALL_C_DEFAULT;
        case CONV_THISCALL:
#ifdef _WIN32
            return DC_CALL_C_X86_WIN32_THIS_MS;
#else
            return DC_CALL_C_X86_WIN32_THIS_GNU;
#endif
#ifdef _WIN32
        case CONV_STDCALL:
            return DC_CALL_C_X86_WIN32_STD;
        case CONV_FASTCALL:
            return DC_CALL_C_X86_WIN32_FAST_MS;
#endif
    }

    return -1;
}

ValveFunction::ValveFunction(void* ulAddr, Convention_t callingConvention, std::vector<DataType_t> args, DataType_t returnType)
    : m_ulAddr(ulAddr)
{
    m_Args = args;

    m_eReturnType = returnType;

    m_eCallingConvention = callingConvention;

    m_iCallingConvention = GetDynCallConvention(m_eCallingConvention);
}

ValveFunction::ValveFunction(void* ulAddr, Convention_t callingConvention, DataType_t* args, int argCount, DataType_t returnType)
    : m_ulAddr(ulAddr)

{
    m_Args = std::vector<DataType_t>(args, args + argCount);
    m_eReturnType = returnType;

    m_eCallingConvention = callingConvention;
    m_iCallingConvention = GetDynCallConvention(m_eCallingConvention);
}

ValveFunction::~ValveFunction()
{
    m_hook.reset();
    if (m_precallback != nullptr)
    {
        globals::callbackManager.ReleaseCallback(m_precallback);
        m_precallback = nullptr;
    }

    if (m_postcallback != nullptr)
    {
        globals::callbackManager.ReleaseCallback(m_postcallback);
        m_postcallback = nullptr;
    }
}

bool ValveFunction::IsCallable() { return (m_eCallingConvention != CONV_CUSTOM) && (m_iCallingConvention != -1); }

template <class ReturnType, class Function> ReturnType CallHelper(Function func, DCCallVM* vm, void* addr)
{
    ReturnType result;
    result = (ReturnType)func(vm, (void*)addr);
    return result;
}

void CallHelperVoid(DCCallVM* vm, void* addr) { dcCallVoid(vm, (void*)addr); }

// Why a call target cannot be called, or nullptr when it looks fine.
//
// A plugin that resolves a pointer out of the server binary's *file image* instead of process memory ends
// up with an unrelocated value. Every .got.plt slot on disk points at the first byte of .plt (the lazy-bind
// resolver stub), and libserver.so is linked BIND_NOW, so that stub's own GOT entries are null: calling it is
// `jmp [0]` and takes the server down with a SIGSEGV at 0x0 in the middle of a game event. The remaining PLT
// entries are real thunks and stay callable. Addresses outside every known module are left alone -- hook
// trampolines and plugin-owned native code live there.
static const char* DescribeUncallableTarget(const void* target)
{
    if (target == nullptr) return "address is null";

    for (const auto& module : modules::moduleList)
    {
        const auto* section = module->FindSectionContaining(target);
        if (section == nullptr) continue;

        if (section->m_szName == ".plt")
        {
            return target == section->m_pBase ? "address is the PLT resolver stub (unrelocated pointer read from the binary on disk?)"
                                              : nullptr;
        }

        static constexpr std::array<std::string_view, 9> data_sections = {
            ".got", ".got.plt", ".data", ".data.rel.ro", ".rodata", ".rdata", ".bss", ".dynamic", ".pdata",
        };
        if (std::find(data_sections.begin(), data_sections.end(), section->m_szName) != data_sections.end())
            return "address points into a data section, not code";

        return nullptr;
    }

    return nullptr;
}

void ValveFunction::Call(ScriptContext& script_context, int offset, bool bypass)
{
    if (!IsCallable()) return;

    auto callVM = std::unique_ptr<DCCallVM, decltype(&dcFree)>(dcNewCallVM(4096), dcFree);
    if (!callVM) throw std::bad_alloc();
    auto* g_pCallVM = callVM.get();
    dcReset(g_pCallVM);
    dcMode(g_pCallVM, m_iCallingConvention);

    for (size_t i = 0; i < m_Args.size(); i++)
    {
        int contextIndex = i + offset;
        switch (m_Args[i])
        {
            case DATA_TYPE_BOOL:
                dcArgBool(g_pCallVM, script_context.GetArgument<bool>(contextIndex));
                break;
            case DATA_TYPE_CHAR:
                dcArgChar(g_pCallVM, script_context.GetArgument<char>(contextIndex));
                break;
            case DATA_TYPE_UCHAR:
                dcArgChar(g_pCallVM, script_context.GetArgument<unsigned char>(contextIndex));
                break;
            case DATA_TYPE_SHORT:
                dcArgShort(g_pCallVM, script_context.GetArgument<short>(contextIndex));
                break;
            case DATA_TYPE_USHORT:
                dcArgShort(g_pCallVM, script_context.GetArgument<unsigned short>(contextIndex));
                break;
            case DATA_TYPE_INT:
                dcArgInt(g_pCallVM, script_context.GetArgument<int>(contextIndex));
                break;
            case DATA_TYPE_UINT:
                dcArgInt(g_pCallVM, script_context.GetArgument<unsigned int>(contextIndex));
                break;
            case DATA_TYPE_LONG:
                dcArgLong(g_pCallVM, script_context.GetArgument<long>(contextIndex));
                break;
            case DATA_TYPE_ULONG:
                dcArgLong(g_pCallVM, script_context.GetArgument<unsigned long>(contextIndex));
                break;
            case DATA_TYPE_LONG_LONG:
                dcArgLongLong(g_pCallVM, script_context.GetArgument<long long>(contextIndex));
                break;
            case DATA_TYPE_ULONG_LONG:
                dcArgLongLong(g_pCallVM, script_context.GetArgument<unsigned long long>(contextIndex));
                break;
            case DATA_TYPE_FLOAT:
                dcArgFloat(g_pCallVM, script_context.GetArgument<float>(contextIndex));
                break;
            case DATA_TYPE_DOUBLE:
                dcArgDouble(g_pCallVM, script_context.GetArgument<double>(contextIndex));
                break;
            case DATA_TYPE_POINTER:
                dcArgPointer(g_pCallVM, script_context.GetArgument<void*>(contextIndex));
                break;
            case DATA_TYPE_STRING:
                dcArgPointer(g_pCallVM, (void*)script_context.GetArgument<const char*>(contextIndex));
                break;
            default:
                assert(!"Unknown function parameter type!");
                break;
        }
    }

    void* m_target = m_ulAddr;
    if (bypass)
    {
        m_target = KHook::FindOriginal(m_ulAddr);
    }

    // m_ulAddr never changes, so the section walk runs once per function; only the null check repeats.
    if (!m_targetChecked)
    {
        m_targetProblem = DescribeUncallableTarget(m_ulAddr);
        m_targetChecked = true;
    }

    if (const char* problem = m_target == nullptr ? "address is null" : m_targetProblem)
    {
        script_context.ThrowNativeError("Refusing to call native function at %p: %s", m_target, problem);
        return;
    }

    switch (m_eReturnType)
    {
        case DATA_TYPE_VOID:
            CallHelperVoid(g_pCallVM, m_target);
            break;
        case DATA_TYPE_BOOL:
            script_context.SetResult(CallHelper<bool>(dcCallChar, g_pCallVM, m_target));
            break;
        case DATA_TYPE_CHAR:
            script_context.SetResult(CallHelper<char>(dcCallChar, g_pCallVM, m_target));
            break;
        case DATA_TYPE_UCHAR:
            script_context.SetResult(CallHelper<unsigned char>(dcCallChar, g_pCallVM, m_target));
            break;
        case DATA_TYPE_SHORT:
            script_context.SetResult(CallHelper<short>(dcCallShort, g_pCallVM, m_target));
            break;
        case DATA_TYPE_USHORT:
            script_context.SetResult(CallHelper<unsigned short>(dcCallShort, g_pCallVM, m_target));
            break;
        case DATA_TYPE_INT:
            script_context.SetResult(CallHelper<int>(dcCallInt, g_pCallVM, m_target));
            break;
        case DATA_TYPE_UINT:
            script_context.SetResult(CallHelper<unsigned int>(dcCallInt, g_pCallVM, m_target));
            break;
        case DATA_TYPE_LONG:
            script_context.SetResult(CallHelper<long>(dcCallLong, g_pCallVM, m_target));
            break;
        case DATA_TYPE_ULONG:
            script_context.SetResult(CallHelper<unsigned long>(dcCallLong, g_pCallVM, m_target));
            break;
        case DATA_TYPE_LONG_LONG:
            script_context.SetResult(CallHelper<long long>(dcCallLongLong, g_pCallVM, m_target));
            break;
        case DATA_TYPE_ULONG_LONG:
            script_context.SetResult(CallHelper<unsigned long long>(dcCallLongLong, g_pCallVM, m_target));
            break;
        case DATA_TYPE_FLOAT:
            script_context.SetResult(CallHelper<float>(dcCallFloat, g_pCallVM, m_target));
            break;
        case DATA_TYPE_DOUBLE:
            script_context.SetResult(CallHelper<double>(dcCallDouble, g_pCallVM, m_target));
            break;
        case DATA_TYPE_POINTER:
            script_context.SetResult(CallHelper<void*>(dcCallPointer, g_pCallVM, m_target));
            break;
        case DATA_TYPE_STRING:
            script_context.SetResult(CallHelper<const char*>(dcCallPointer, g_pCallVM, m_target));
            break;
        default:
            assert(!"Unknown function return type!");
            break;
    }
}

KHook::Action ValveFunction::DispatchHook(bool post, DynamicHookContext& hook)
{
    // Managed native access is restricted to the game thread. Do not enter the
    // CLR callback path from an engine worker thread.
    if (globals::gameThreadId != std::this_thread::get_id()) return KHook::Action::Ignore;
    if (post && KHook::WasOriginalFunctionSkipped()) return KHook::Action::Ignore;

    auto* callback = post ? m_postcallback : m_precallback;
    auto nativeCallback = m_callback;
    HookResult result = HookResult::Continue;
    if (nativeCallback) result = (*nativeCallback)(post ? HookMode::Post : HookMode::Pre, hook);

    // Each invocation owns its script context. Nested calls must not reset the
    // outer callback's argument and result buffers.
    auto functions = callback ? callback->GetFunctions() : std::vector<CallbackT>{};
    for (auto function : functions)
    {
        if (result >= HookResult::Stop) break;
        if (!function) continue;
        fxNativeContext raw{};
        ScriptContextRaw context(raw);
        context.Reset();
        context.Push(&hook);
        function(&raw);
        result = (std::max)(result, context.GetResult<HookResult>());
    }
    return result >= HookResult::Handled ? KHook::Action::Supersede : KHook::Action::Ignore;
}

void ValveFunction::EnsureHook()
{
    if (!m_hook)
        m_hook = std::make_unique<DynamicHook>(m_ulAddr, m_Args, m_eReturnType, [this](bool post, DynamicHookContext& hook) {
            return DispatchHook(post, hook);
        });
}

void ValveFunction::AddHook(const std::function<HookResult(HookMode, DynamicHookContext&)>& callback)
{
    EnsureHook();
    m_callback = callback;
}

void ValveFunction::AddHook(CallbackT callable, bool post)
{
    EnsureHook();
    auto*& callback = post ? m_postcallback : m_precallback;
    if (!callback) callback = globals::callbackManager.CreateCallback("");
    callback->AddListener(callable);
}

void ValveFunction::RemoveHook(CallbackT callable, bool post)
{
    auto* callback = post ? m_postcallback : m_precallback;
    if (callback) callback->RemoveListener(callable);
    if ((!m_precallback || !m_precallback->GetFunctionCount()) && (!m_postcallback || !m_postcallback->GetFunctionCount()) && !m_callback)
        m_hook.reset();
}

} // namespace counterstrikesharp
