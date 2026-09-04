/*
 *  This file is part of CounterStrikeSharp.
 *  CounterStrikeSharp is free software: you can redistribute it and/or modify
 *  it under the terms of the GNU General Public License as published by
 *  the Free Software Foundation, either version 3 of the License, or
 *  (at your option) any later version.
 *
 *  CounterStrikeSharp is distributed in the hope that it will be useful,
 *  but WITHOUT ANY WARRANTY; without even the implied warranty of
 *  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 *  GNU General Public License for more details.
 *
 *  You should have received a copy of the GNU General Public License
 *  along with CounterStrikeSharp.  If not, see <https://www.gnu.org/licenses/>. *
 */

#include "scripting/callback_manager.h"

#include <algorithm>

#include "core/fatal_reporter.h"
#include "core/log.h"
#include "vprof.h"

namespace counterstrikesharp {

ScriptCallback::ScriptCallback(const char* szName) : m_root_context(fxNativeContext{})
{
    m_script_context_raw = ScriptContextRaw(m_root_context);
    m_name = std::string(szName);
    m_profile_name = "ScriptCallback::Execute::" + m_name;
}

ScriptCallback::~ScriptCallback() { m_functions.clear(); }

void ScriptCallback::AddListener(CallbackT fnPluginFunction) { m_functions.push_back(fnPluginFunction); }

bool ScriptCallback::RemoveListener(CallbackT fnPluginFunction)
{
    size_t nOriginalSize = m_functions.size();
    m_functions.erase(std::ranges::remove(m_functions, fnPluginFunction).begin(), m_functions.end());
    return m_functions.size() != nOriginalSize;
}

bool ScriptCallback::IsContextSafe()
{
    // The previous try/catch around GetResult<void*>() on a POD struct cannot throw,
    // so the compiler folded the whole check to `return true`. Check the one field
    // that actually goes bad when a context is corrupt or reused without Reset().
    const int nArguments = m_root_context.numArguments;

    if (nArguments < 0 || nArguments > ScriptContext::MaxArguments)
    {
        CSSHARP_CORE_WARN("Context of callback '{}' is corrupt, numArguments is {}", m_name, nArguments);

        return false;
    }

    return true;
}

void ScriptCallback::Execute(bool bResetContext)
{
    if (!IsContextSafe())
    {
        ScriptContext().ThrowNativeError("ScriptCallback::Execute aborted due to invalid context");
        CSSHARP_CORE_WARN("ScriptCallback::Execute aborted due to invalid context (callback: '{}')", m_name);
        return;
    }

    // VPROF_BUDGET(m_profile_name.c_str(), "CS# Script Callbacks");

    const size_t nCount = m_functions.size();

    for (size_t nI = 0; nI < nCount; ++nI)
    {
        // A listener may remove itself (or others) while we iterate.
        if (nI >= m_functions.size()) break;

        if (auto fnMethodToCall = m_functions[nI])
        {
            // Record which native->managed call we are about to make. If that call
            // FailFasts on a garbage-collected delegate (abort() -> SIGABRT), the
            // fatal handler prints this as the last console line so the culprit is
            // named instead of an anonymous "Process terminated".
            fatal::SetCallbackBreadcrumb(m_name.c_str(), (int)nI);
            try
            {
                fnMethodToCall(&ScriptContextStruct());
            }
            catch (...)
            {
                ScriptContext().ThrowNativeError("Exception in callback execution");
                CSSHARP_CORE_ERROR("Exception thrown inside callback '{}', index {}", m_name, nI);
            }
        }
        else
        {
            ScriptContext().ThrowNativeError("Null listener in callback");
            CSSHARP_CORE_ERROR("Null function pointer in callback '{}', index {}", m_name, nI);
        }
    }

    fatal::ClearCallbackBreadcrumb();

    if (bResetContext)
    {
        Reset();
    }
}

void ScriptCallback::Reset() { ScriptContext().Reset(); }

CallbackManager::CallbackManager() = default;

ScriptCallback* CallbackManager::CreateCallback(const char* szName)
{
    CSSHARP_CORE_TRACE("Creating callback {0}", szName);
    auto* pCallback = new ScriptCallback(szName);
    m_managed.push_back(pCallback);

    return pCallback;
}

ScriptCallback* CallbackManager::FindCallback(const char* szName)
{
    for (auto* pMarshal : m_managed)
    {
        if (strcmp(pMarshal->GetName().c_str(), szName) == 0)
        {
            return pMarshal;
        }
    }

    return nullptr;
}

void CallbackManager::ReleaseCallback(ScriptCallback* pCallback)
{
    auto I = std::ranges::remove_if(m_managed, [pCallback](const ScriptCallback* pI) {
        return pCallback == pI;
    }).begin();

    if (I != m_managed.end()) m_managed.erase(I, m_managed.end());
    delete pCallback;
}

bool CallbackManager::TryAddFunction(const char* szName, CallbackT fnCallable)
{
    if (auto* pCallback = FindCallback(szName))
    {
        pCallback->AddListener(fnCallable);
        return true;
    }

    return false;
}

bool CallbackManager::TryRemoveFunction(const char* szName, CallbackT fnCallable)
{
    if (auto* pCallback = FindCallback(szName))
    {
        return pCallback->RemoveListener(fnCallable);
    }

    return false;
}

void CallbackManager::PrintCallbackDebug()
{
    CSSHARP_CORE_INFO("----CALLBACKS----");
    for (auto* pCallback : m_managed)
    {
        CSSHARP_CORE_INFO("{0} ({0})\n", pCallback->GetName().c_str(), 1);
    }
}
CallbackPair::CallbackPair()
{
    pre = globals::callbackManager.CreateCallback("");
    post = globals::callbackManager.CreateCallback("");
}

CallbackPair::CallbackPair(bool bNoCallbacks)
{
    if (!bNoCallbacks)
    {
        pre = globals::callbackManager.CreateCallback("");
        post = globals::callbackManager.CreateCallback("");
    }
}

CallbackPair::~CallbackPair()
{
    globals::callbackManager.ReleaseCallback(pre);
    globals::callbackManager.ReleaseCallback(post);
}

} // namespace counterstrikesharp
