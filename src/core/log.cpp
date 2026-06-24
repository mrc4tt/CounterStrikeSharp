#include "core/log.h"

#include <cctype>
#include <chrono>
#include <filesystem>

#include <spdlog/async.h>
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

    // Async logging so disk/console IO never runs on the caller (game) thread.
    // A synchronous logger with flush_on(info) wrote+fsync'd on whatever thread
    // logged -- any info-level line on the per-tick path stalled the game thread
    // for the duration of the write => frame spikes. The async_logger hands the
    // formatted message to a background worker instead.
    //
    // overrun_oldest: if the queue ever fills, drop the OLDEST queued message
    // rather than block the producer. Blocking here would reintroduce the exact
    // stall we are removing, so we trade (rare, only-under-flood) lost log lines
    // for a game thread that never waits on logging.
    spdlog::init_thread_pool(8192, 1);
    m_core_logger =
        std::make_shared<spdlog::async_logger>("CSSharp", color_sink, spdlog::thread_pool(), spdlog::async_overflow_policy::overrun_oldest);
    spdlog::register_logger(m_core_logger);
    m_core_logger->set_level(spdlog::level::info);
    // Flush only on warn+ instead of every info line. Combined with the periodic
    // flush below, info still reaches disk within ~2s without an fsync per line.
    m_core_logger->flush_on(spdlog::level::warn);
    spdlog::flush_every(std::chrono::seconds(2));

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
    // Keep flush trigger at warn+ regardless of verbosity. flush_on(parsed) at
    // info/debug/trace would fsync on (almost) every line, defeating the async
    // logger. The periodic flush_every set in Init() still drains lower levels.
    m_core_logger->flush_on(parsed < spdlog::level::warn ? spdlog::level::warn : parsed);
}

void Log::Close()
{
    // Flush the async queue and join the worker thread BEFORE dropping the
    // logger. Otherwise the background worker could still be writing into sinks
    // that are being torn down on unload -> use-after-free. shutdown() drains
    // the thread pool and stops the worker.
    if (m_core_logger) m_core_logger->flush();
    spdlog::shutdown();
    m_core_logger = nullptr;
}
} // namespace counterstrikesharp
