/*
 * Copyright (c) 2014 Bas Timmer/NTAuthority et al.
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction, including without limitation the rights
 * to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the Software is
 * furnished to do so, subject to the following conditions:
 *
 * The above copyright notice and this permission notice shall be included in
 * all copies or substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
 * THE SOFTWARE.
 *
 * This file has been modified from its original form for use in this program
 * under GNU Lesser General Public License, version 2.
 */

#include "scripting/script_engine.h"

#include <stack>
#include <unordered_map>
#include <unordered_set>

#include "core/log.h"
#include "core/utils.h"

static std::unordered_map<uint64_t, counterstrikesharp::TNativeHandler> g_registeredHandlers;
static std::unordered_map<uint64_t, std::string> g_registeredNames;

namespace counterstrikesharp {

std::stack<std::string> errors;

void ScriptContext::ThrowNativeError(const char* msg, ...)
{
    va_list arglist;
    char buff[256];
    va_start(arglist, msg);
    vsnprintf(buff, sizeof(buff), msg, arglist);
    va_end(arglist);

    auto error_string = std::string(buff);
    errors.push(error_string);

    const char* ptr = errors.top().c_str();
    this->SetResult(ptr);
    *this->m_has_error = 1;
}

void ScriptContext::Reset()
{
    if (*m_has_error)
    {
        errors.pop();
    }

    m_numResults = 0;
    m_numArguments = 0;
    *m_has_error = 0;

    // Push() increments BOTH the local m_numArguments and the shared
    // m_native_context->numArguments, so resetting only the local copy let the
    // struct's counter grow by one per push forever. After 32 pushes it exceeds
    // ScriptContext::MaxArguments and ScriptCallback::IsContextSafe() rejects every
    // subsequent Execute() -- "Context of callback 'OnEntityDeleted' is corrupt,
    // numArguments is 321" and climbing, on a callback that only ever pushes one
    // argument. The managed side reads numArguments out of this same struct, so the
    // drift is wrong regardless of the guard.
    m_native_context->numArguments = 0;
    m_native_context->numResults = 0;

    for (int i = 0; i < 32; i++)
    {
        m_native_context->arguments[i] = 0;
    }

    m_native_context->result = 0;
}

tl::optional<TNativeHandler> ScriptEngine::GetNativeHandler(uint64_t nativeIdentifier)
{
    auto it = g_registeredHandlers.find(nativeIdentifier);

    if (it != g_registeredHandlers.end())
    {
        return it->second;
    }

    return tl::optional<TNativeHandler>();
}

tl::optional<TNativeHandler> ScriptEngine::GetNativeHandler(std::string identifier)
{
    auto it = g_registeredHandlers.find(hash_string(identifier.c_str()));

    if (it != g_registeredHandlers.end())
    {
        return it->second;
    }

    return tl::optional<TNativeHandler>();
}

bool ScriptEngine::CallNativeHandler(uint64_t nativeIdentifier, ScriptContext& context)
{
    // Call through the map entry instead of copying the std::function into an optional first.
    auto it = g_registeredHandlers.find(nativeIdentifier);
    if (it == g_registeredHandlers.end()) return false;

    it->second(context);
    return true;
}

void ScriptEngine::RegisterNativeHandlerInt(uint64_t nativeIdentifier, TNativeHandler function)
{
    g_registeredHandlers[nativeIdentifier] = function;
}

void ScriptEngine::RegisterNativeName(uint64_t nativeIdentifier, const char* nativeName)
{
    g_registeredNames[nativeIdentifier] = nativeName;
}

const char* ScriptEngine::GetNativeName(uint64_t nativeIdentifier)
{
    auto it = g_registeredNames.find(nativeIdentifier);
    return it != g_registeredNames.end() ? it->second.c_str() : "?";
}

void ScriptEngine::InvokeNative(counterstrikesharp::fxNativeContext& context)
{
    if (context.nativeIdentifier == 0) return;

    // Every managed -> native call lands here. Call through the map entry instead of copying the
    // std::function into an optional on each call.
    auto it = g_registeredHandlers.find(context.nativeIdentifier);

    if (it != g_registeredHandlers.end())
    {
        counterstrikesharp::ScriptContextRaw scriptContext(context);

        it->second(scriptContext);
    }
    else
    {
        // Throw instead of returning: the caller used to get a zeroed result (null pointer,
        // 0, false) and carry on with it. Typically a plugin built against a newer API than
        // this core. Logged once per id; a plugin calling it every tick flooded the log.
        counterstrikesharp::ScriptContextRaw scriptContext(context);
        scriptContext.ThrowNativeError("Native %llx is not available in this CounterStrikeSharp build",
                                       static_cast<unsigned long long>(context.nativeIdentifier));

        static std::unordered_set<uint64_t> s_warned;
        if (s_warned.insert(context.nativeIdentifier).second)
        {
            CSSHARP_CORE_WARN("Native Handler was requested but not found: {0:x} (plugin built against a newer API?)",
                              context.nativeIdentifier);
        }
    }
}

ScriptContextRaw ScriptEngine::m_context;

} // namespace counterstrikesharp
