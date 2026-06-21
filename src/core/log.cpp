#include "core/log.h"

#include <cctype>
#include <filesystem>

#include <spdlog/sinks/basic_file_sink.h>
#include <spdlog/sinks/stdout_color_sinks.h>
#include <spdlog/cfg/env.h>

namespace counterstrikesharp {
std::shared_ptr<spdlog::logger> Log::m_core_logger;

void Log::Init()
{
    // Console sink only. The file sink is attached later via AttachFileSink once
    // the addons root is known — Init() runs before the engine interfaces (and
    // thus the root directory) are available, and a relative log path here would
    // resolve against the engine's working directory, which the server user may
    // not own (the cause of the historical "Permission denied" startup crash).
    auto color_sink = std::make_shared<spdlog::sinks::stderr_color_sink_mt>();
#if _WIN32
    color_sink->set_color(spdlog::level::trace, 6);
#else
    color_sink->set_color(spdlog::level::trace, color_sink->yellow);
#endif
    color_sink->set_pattern("%^[%T.%e] [%l] %n: %v%$");

    m_core_logger = std::make_shared<spdlog::logger>("CSSharp", color_sink);
    spdlog::register_logger(m_core_logger);
    m_core_logger->set_level(spdlog::level::info);
    m_core_logger->flush_on(spdlog::level::info);

    spdlog::cfg::load_env_levels();
}

void Log::AttachFileSink(const std::string& logDirectory)
{
    if (!m_core_logger) return;

    // A failure to open the log file must NOT take down the server. Degrade to
    // console-only logging and warn, rather than letting spdlog's exception
    // propagate into std::terminate.
    try
    {
        std::filesystem::create_directories(logDirectory);
        const std::string path = logDirectory + "/counterstrikesharp.log";

        auto file_sink = std::make_shared<spdlog::sinks::basic_file_sink_mt>(path, true);
        file_sink->set_pattern("[%T.%e] [%l] %n: %v");
        m_core_logger->sinks().push_back(file_sink);

        CSSHARP_CORE_DEBUG("Logging to file: {}", path);
    }
    catch (const std::exception& ex)
    {
        CSSHARP_CORE_WARN("Could not open log file in '{}' ({}). Continuing with console logging only.", logDirectory, ex.what());
    }
}

void Log::SetLevelFromString(const std::string& level)
{
    if (!m_core_logger) return;

    std::string l;
    l.reserve(level.size());
    for (char c : level)
        l.push_back((char)std::tolower((unsigned char)c));

    spdlog::level::level_enum parsed;
    if (l == "verbose" || l == "trace") parsed = spdlog::level::trace;
    else if (l == "debug")
        parsed = spdlog::level::debug;
    else if (l == "information" || l == "info")
        parsed = spdlog::level::info;
    else if (l == "warning" || l == "warn")
        parsed = spdlog::level::warn;
    else if (l == "error")
        parsed = spdlog::level::err;
    else if (l == "critical" || l == "fatal")
        parsed = spdlog::level::critical;
    else if (l == "off" || l == "none")
        parsed = spdlog::level::off;
    else
        parsed = spdlog::level::info;

    m_core_logger->set_level(parsed);
    m_core_logger->flush_on(parsed);
}

void Log::Close()
{
    spdlog::drop("CSSharp");
    m_core_logger = nullptr;
}
} // namespace counterstrikesharp
