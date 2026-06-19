#pragma once

#include <memory>
#include <string>

#include <spdlog/fmt/ostr.h>
#include <spdlog/spdlog.h>

namespace counterstrikesharp {
class Log
{
  public:
    static void Init();
    static void Close();

    // Attaches a file sink writing to <logDirectory>/counterstrikesharp.log. Safe to
    // call after the addons root is resolved; on any failure (e.g. permissions) it
    // logs a warning and leaves the logger console-only instead of throwing.
    static void AttachFileSink(const std::string& logDirectory);

    // Sets the core logger level from a config string. Accepts spdlog and Serilog
    // spellings (verbose/trace, debug, information/info, warning/warn, error,
    // critical/fatal, off). Unknown values fall back to info.
    static void SetLevelFromString(const std::string& level);

    static std::shared_ptr<spdlog::logger>& GetCoreLogger() { return m_core_logger; }

  private:
    static std::shared_ptr<spdlog::logger> m_core_logger;
};
} // namespace counterstrikesharp

#define CSSHARP_CORE_TRACE(...)    ::counterstrikesharp::Log::GetCoreLogger()->trace(__VA_ARGS__)
#define CSSHARP_CORE_DEBUG(...)    ::counterstrikesharp::Log::GetCoreLogger()->debug(__VA_ARGS__)
#define CSSHARP_CORE_INFO(...)     ::counterstrikesharp::Log::GetCoreLogger()->info(__VA_ARGS__)
#define CSSHARP_CORE_WARN(...)     ::counterstrikesharp::Log::GetCoreLogger()->warn(__VA_ARGS__)
#define CSSHARP_CORE_ERROR(...)    ::counterstrikesharp::Log::GetCoreLogger()->error(__VA_ARGS__)
#define CSSHARP_CORE_CRITICAL(...) ::counterstrikesharp::Log::GetCoreLogger()->critical(__VA_ARGS__)
