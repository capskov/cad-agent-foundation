// Modified for CAD Agent Foundation by capskov (2026); derived from Bimwright DWG MCP pinned at 4d0f75646631186612af926944d7e33affc4757d. See NOTICE.md.
using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Bimwright.Dwg.Shared;

namespace Bimwright.Dwg.Server
{
    public class DwgMcpConfig
    {
        public const string EnvTarget = "BIMWRIGHT_DWG_TARGET";
        public const string EnvToolsets = "BIMWRIGHT_DWG_TOOLSETS";
        public const string EnvReadOnly = "BIMWRIGHT_DWG_READ_ONLY";
        public const string EnvEnableToolbaker = "BIMWRIGHT_DWG_ENABLE_TOOLBAKER";
        public const string EnvAllowLanBind = "BIMWRIGHT_DWG_ALLOW_LAN_BIND";
        public const string EnvLogLevel = "BIMWRIGHT_DWG_LOG_LEVEL";
        public const string EnvEnableD1WritePilot = "BIMWRIGHT_DWG_ENABLE_D1_WRITE_PILOT";
        public const string EnvD1ExpectedDrawingPath = "BIMWRIGHT_DWG_D1_EXPECTED_DRAWING_PATH";
        public const string EnvD1ExpectedJournalScopeId = "BIMWRIGHT_DWG_D1_EXPECTED_JOURNAL_SCOPE_ID";
        public const string EnvD1ExpectedJournalPathDigest = "BIMWRIGHT_DWG_D1_EXPECTED_JOURNAL_PATH_DIGEST";
        public const string EnvEnableD1PreflightPilot = "BIMWRIGHT_DWG_ENABLE_D1_PREFLIGHT_PILOT";
        public const string EnvEnableH1WritePilot = "BIMWRIGHT_DWG_ENABLE_H1_WRITE_PILOT";
        public const string EnvH1J10PlanFile = "BIMWRIGHT_DWG_H1_J10_PLAN_FILE";
        public const string EnvEnableFoundationWrite = "BIMWRIGHT_DWG_ENABLE_FOUNDATION_WRITE";
        public const string EnvEnableH1PreflightPilot = "BIMWRIGHT_DWG_ENABLE_H1_PREFLIGHT_PILOT";
        public const string EnvH1ExpectedDrawingPath = "BIMWRIGHT_DWG_H1_EXPECTED_DRAWING_PATH";
        public const string EnvH1ExpectedJournalScopeId = "BIMWRIGHT_DWG_H1_EXPECTED_JOURNAL_SCOPE_ID";
        public const string EnvH1ExpectedJournalPathDigest = "BIMWRIGHT_DWG_H1_EXPECTED_JOURNAL_PATH_DIGEST";
        public const string EnvD1PreflightExpectedDrawingPath = "BIMWRIGHT_DWG_D1_PREFLIGHT_EXPECTED_DRAWING_PATH";
        public const string EnvD1PreflightExpectedJournalScopeId = "BIMWRIGHT_DWG_D1_PREFLIGHT_EXPECTED_JOURNAL_SCOPE_ID";
        public const string EnvD1PreflightExpectedJournalPathDigest = "BIMWRIGHT_DWG_D1_PREFLIGHT_EXPECTED_JOURNAL_PATH_DIGEST";

        public string Target { get; set; }
        public List<string> Toolsets { get; set; }
        public bool? ReadOnly { get; set; }
        public bool? EnableToolbaker { get; set; }
        public bool? AllowLanBind { get; set; }
        public bool? EnableD1WritePilot { get; set; }
        public string D1WriteExpectedDrawingPath { get; set; }
        public string D1WriteExpectedJournalScopeId { get; set; }
        public string D1WriteExpectedJournalPathDigest { get; set; }
        public bool? EnableD1PreflightPilot { get; set; }
        public bool? EnableH1PreflightPilot { get; set; }
        public bool? EnableH1WritePilot { get; set; }
        public string H1J10PlanFile { get; set; }
        public bool? EnableFoundationWrite { get; set; }
        public string FoundationExpectedDrawingPath { get; set; }
        public string FoundationExpectedJournalScopeId { get; set; }
        public string FoundationExpectedJournalPathDigest { get; set; }
        public string H1ExpectedDrawingPath { get; set; }
        public string H1ExpectedJournalScopeId { get; set; }
        public string H1ExpectedJournalPathDigest { get; set; }
        public string D1PreflightExpectedDrawingPath { get; set; }
        public string D1PreflightExpectedJournalScopeId { get; set; }
        public string D1PreflightExpectedJournalPathDigest { get; set; }
        public string LogLevel { get; set; }

        /// <summary>
        /// Default config file the installer seeds and the server reads when no
        /// --config is passed: the historical %LOCALAPPDATA%\Bimwright\Dwg location by default;
        /// BIMWRIGHT_DWG_ROOT changes this to <root>\Dwg.
        /// </summary>
        public static string DefaultConfigFilePath => BimwrightAppRoot.Current.UnderDwg("dwgmcp.config.json");

        [JsonIgnore] public bool ReadOnlyOrDefault => ReadOnly ?? false;
        [JsonIgnore] public bool EnableToolbakerOrDefault => EnableToolbaker ?? true;
        [JsonIgnore] public bool AllowLanBindOrDefault => AllowLanBind ?? false;
        [JsonIgnore] public bool EnableD1WritePilotOrDefault => EnableD1WritePilot ?? false;
        [JsonIgnore] public bool EnableD1PreflightPilotOrDefault => EnableD1PreflightPilot ?? false;
        [JsonIgnore] public bool EnableH1WritePilotOrDefault => EnableH1WritePilot ?? false;
        [JsonIgnore] public bool EnableFoundationWriteOrDefault => EnableFoundationWrite ?? false;
        [JsonIgnore] public bool EnableH1PreflightPilotOrDefault => EnableH1PreflightPilot ?? false;

        public static DwgMcpConfig Load(
            string[] args = null,
            string configFilePath = null,
            Func<string, string> envLookup = null)
        {
            args = args ?? Array.Empty<string>();
            envLookup = envLookup ?? Environment.GetEnvironmentVariable;

            var cliConfig = GetOptionValue(args, "--config");
            var path = !string.IsNullOrWhiteSpace(cliConfig)
                ? cliConfig
                : (configFilePath ?? DefaultConfigFilePath);
            var config = LoadJson(path);

            ApplyEnv(config, envLookup);
            ApplyCli(config, args);
            return config;
        }

        private static DwgMcpConfig LoadJson(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return new DwgMcpConfig();
            }

            var json = File.ReadAllText(path);
            return JsonConvert.DeserializeObject<DwgMcpConfig>(json) ?? new DwgMcpConfig();
        }

        private static void ApplyEnv(DwgMcpConfig config, Func<string, string> envLookup)
        {
            ApplyString(envLookup(EnvTarget), value => config.Target = value);
            ApplyCsv(envLookup(EnvToolsets), value => config.Toolsets = value);
            ApplyBool(envLookup(EnvReadOnly), value => config.ReadOnly = value);
            ApplyBool(envLookup(EnvEnableToolbaker), value => config.EnableToolbaker = value);
            ApplyBool(envLookup(EnvAllowLanBind), value => config.AllowLanBind = value);
            ApplyBool(envLookup(EnvEnableD1WritePilot), value => config.EnableD1WritePilot = value);
            ApplyString(envLookup(EnvD1ExpectedDrawingPath), value => config.D1WriteExpectedDrawingPath = value);
            ApplyString(envLookup(EnvD1ExpectedJournalScopeId), value => config.D1WriteExpectedJournalScopeId = value);
            ApplyString(envLookup(EnvD1ExpectedJournalPathDigest), value => config.D1WriteExpectedJournalPathDigest = value);
            ApplyBool(envLookup(EnvEnableD1PreflightPilot), value => config.EnableD1PreflightPilot = value);
            ApplyBool(envLookup(EnvEnableH1PreflightPilot), value => config.EnableH1PreflightPilot = value);
            ApplyBool(envLookup(EnvEnableH1WritePilot), value => config.EnableH1WritePilot = value);
            ApplyString(envLookup(EnvH1J10PlanFile), value => config.H1J10PlanFile = value);
            ApplyBool(envLookup(EnvEnableFoundationWrite), value => config.EnableFoundationWrite = value);
            ApplyString(envLookup("BIMWRIGHT_DWG_FOUNDATION_EXPECTED_DRAWING_PATH"),value=>config.FoundationExpectedDrawingPath=value);
            ApplyString(envLookup("BIMWRIGHT_DWG_FOUNDATION_EXPECTED_JOURNAL_SCOPE_ID"),value=>config.FoundationExpectedJournalScopeId=value);
            ApplyString(envLookup("BIMWRIGHT_DWG_FOUNDATION_EXPECTED_JOURNAL_PATH_DIGEST"),value=>config.FoundationExpectedJournalPathDigest=value);
            ApplyString(envLookup(EnvH1ExpectedDrawingPath), value => config.H1ExpectedDrawingPath = value);
            ApplyString(envLookup(EnvH1ExpectedJournalScopeId), value => config.H1ExpectedJournalScopeId = value);
            ApplyString(envLookup(EnvH1ExpectedJournalPathDigest), value => config.H1ExpectedJournalPathDigest = value);
            ApplyString(envLookup(EnvD1PreflightExpectedDrawingPath), value => config.D1PreflightExpectedDrawingPath = value);
            ApplyString(envLookup(EnvD1PreflightExpectedJournalScopeId), value => config.D1PreflightExpectedJournalScopeId = value);
            ApplyString(envLookup(EnvD1PreflightExpectedJournalPathDigest), value => config.D1PreflightExpectedJournalPathDigest = value);
            ApplyString(envLookup(EnvLogLevel), value => config.LogLevel = value);
        }

        private static void ApplyCli(DwgMcpConfig config, string[] args)
        {
            ApplyString(GetOptionValue(args, "--target"), value => config.Target = value);
            ApplyCsv(GetOptionValue(args, "--toolsets"), value => config.Toolsets = value);
            ApplyString(GetOptionValue(args, "--log-level"), value => config.LogLevel = value);

            if (HasFlag(args, "--read-only"))
            {
                config.ReadOnly = true;
            }

            if (HasFlag(args, "--enable-toolbaker"))
            {
                config.EnableToolbaker = true;
            }

            if (HasFlag(args, "--disable-toolbaker"))
            {
                config.EnableToolbaker = false;
            }

            if (HasFlag(args, "--allow-lan-bind"))
            {
                config.AllowLanBind = true;
            }

            if (HasFlag(args, "--enable-d1-write-pilot"))
            {
                config.EnableD1WritePilot = true;
            }

            if (HasFlag(args, "--disable-d1-write-pilot"))
            {
                config.EnableD1WritePilot = false;
            }

            if (HasFlag(args, "--enable-d1-preflight-pilot"))
            {
                config.EnableD1PreflightPilot = true;
            }

            if (HasFlag(args, "--disable-d1-preflight-pilot"))
            {
                config.EnableD1PreflightPilot = false;
            }
            if (HasFlag(args, "--enable-h1-preflight-pilot")) config.EnableH1PreflightPilot = true;
            if (HasFlag(args, "--disable-h1-preflight-pilot")) config.EnableH1PreflightPilot = false;
            if (HasFlag(args, "--enable-h1-write-pilot")) config.EnableH1WritePilot = true;
            if (HasFlag(args, "--enable-foundation-write")) config.EnableFoundationWrite = true;
            if (HasFlag(args, "--disable-foundation-write")) config.EnableFoundationWrite = false;
            if (HasFlag(args, "--disable-h1-write-pilot")) config.EnableH1WritePilot = false;
        }

        private static void ApplyString(string raw, Action<string> set)
        {
            if (!string.IsNullOrWhiteSpace(raw))
            {
                set(raw.Trim());
            }
        }

        private static void ApplyCsv(string raw, Action<List<string>> set)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return;
            }

            var values = new List<string>();
            foreach (var item in raw.Split(','))
            {
                var trimmed = item.Trim();
                if (trimmed.Length > 0)
                {
                    values.Add(trimmed);
                }
            }
            set(values);
        }

        private static void ApplyBool(string raw, Action<bool> set)
        {
            if (TryParseBool(raw, out var value))
            {
                set(value);
            }
        }

        private static bool TryParseBool(string raw, out bool value)
        {
            value = false;
            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            switch (raw.Trim().ToLowerInvariant())
            {
                case "1":
                case "true":
                case "yes":
                case "on":
                    value = true;
                    return true;
                case "0":
                case "false":
                case "no":
                case "off":
                    value = false;
                    return true;
                default:
                    return false;
            }
        }

        private static bool HasFlag(string[] args, string name)
        {
            foreach (var arg in args)
            {
                if (string.Equals(arg, name, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static string GetOptionValue(string[] args, string name)
        {
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                {
                    return args[i + 1];
                }
            }
            return null;
        }
    }
}
