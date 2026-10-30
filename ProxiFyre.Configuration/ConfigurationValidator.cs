using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace ProxiFyre.Configuration
{
    /// <summary>
    /// Returns structured diagnostics without logging or changing the supplied model.
    /// </summary>
    public sealed class ConfigurationValidator
    {
        /// <summary>
        /// RFC 1929 stores each authentication field behind a single-byte length.
        /// The C++/CLI bridge marshals managed credentials through the Windows ANSI
        /// encoding before enforcing this limit.
        /// </summary>
        public const int Socks5CredentialMaximumEncodedBytes = 255;

        private const uint ThreadAnsiCodePage = 3;
        private const uint NoBestFitCharacters = 0x00000400;
        private static readonly string[] SupportedLogLevels = { "Error", "Warning", "Info", "Debug", "All" };

        public ValidationResult Validate(ProxiFyreConfiguration configuration)
        {
            var issues = new List<ValidationIssue>();
            if (configuration == null)
            {
                issues.Add(Error("CONFIGURATION_NULL", "配置不包含任何设置。", null));
                return new ValidationResult(issues);
            }

            ValidateLogLevel(configuration.LogLevel, issues);
            ValidateExclusions(configuration.Excludes, issues);

            if (configuration.Proxies == null)
            {
                issues.Add(Error("PROXIES_MISSING", "缺少必需的 proxies 属性。", "proxies"));
                return new ValidationResult(issues);
            }
            if (configuration.Proxies.Count == 0)
            {
                issues.Add(Error("PROXIES_EMPTY", "必须至少配置一条代理规则。", "proxies"));
                return new ValidationResult(issues);
            }

            for (var index = 0; index < configuration.Proxies.Count; index++)
            {
                var rule = configuration.Proxies[index];
                if (rule == null)
                {
                    issues.Add(Error(
                        "PROXY_NULL",
                        "代理列表包含空条目。请移除它或替换为有效规则。",
                        RulePath(index),
                        index));
                    continue;
                }

                ValidateSingleRule(rule, index, issues);
            }

            ValidateCatchAllOrdering(configuration.Proxies, issues);
            ValidateApplicationOverlap(configuration.Proxies, issues);
            return new ValidationResult(issues);
        }

        public ValidationResult ValidateRule(ProxyRule rule, int? proxyRuleIndex = null)
        {
            var issues = new List<ValidationIssue>();
            if (rule == null)
            {
                issues.Add(Error(
                    "PROXY_NULL",
                    "代理规则为空。",
                    proxyRuleIndex.HasValue ? RulePath(proxyRuleIndex.Value) : "proxy",
                    proxyRuleIndex));
            }
            else
            {
                ValidateSingleRule(rule, proxyRuleIndex, issues);
            }
            return new ValidationResult(issues);
        }

        private static void ValidateLogLevel(string value, ICollection<ValidationIssue> issues)
        {
            if (string.IsNullOrWhiteSpace(value))
                return; // The engine's established fallback is Info.

            if (!SupportedLogLevels.Any(level => string.Equals(level, value.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                issues.Add(Warning(
                    "LOG_LEVEL_UNKNOWN",
                    "无法识别该日志级别，引擎将使用 Info。支持的值包括 Error、Warning、Info、Debug 和 All。",
                    "logLevel"));
            }
        }

        private static void ValidateExclusions(IList<string> excludes, ICollection<ValidationIssue> issues)
        {
            if (excludes == null)
                return;

            for (var index = 0; index < excludes.Count; index++)
            {
                if (string.IsNullOrWhiteSpace(excludes[index]))
                {
                    issues.Add(Warning(
                        "EXCLUSION_BLANK",
                        "空值和仅包含空白的排除项会被忽略。",
                        "excludes[" + index + "]"));
                }
            }
        }

        private static void ValidateSingleRule(ProxyRule rule, int? index, ICollection<ValidationIssue> issues)
        {
            var prefix = index.HasValue ? RulePath(index.Value) : "proxy";

            ProxyEndpoint endpoint;
            EndpointParseError endpointError;
            if (!EndpointParser.TryParse(rule.Socks5ProxyEndpoint, out endpoint, out endpointError))
            {
                var code = "ENDPOINT_INVALID";
                var message = "SOCKS5 端点必须包含主机名或 IPv4 地址，以及 1 到 65535 之间的端口。";
                if (endpointError == EndpointParseError.Missing)
                {
                    code = "ENDPOINT_MISSING";
                    message = "必须提供非空的 SOCKS5 代理端点。";
                }
                else if (endpointError == EndpointParseError.InvalidPort || endpointError == EndpointParseError.MissingPort)
                {
                    code = "ENDPOINT_PORT_INVALID";
                    message = "SOCKS5 端点端口必须是 1 到 65535 之间的整数。";
                }
                else if (endpointError == EndpointParseError.Ipv6UpstreamNotSupported)
                {
                    code = "ENDPOINT_IPV6_UNSUPPORTED";
                    message = "当前引擎无法连接 IPv6 字面量的 SOCKS5 端点；请使用 IPv4 地址或具有 A 记录的主机名。";
                }

                issues.Add(Error(code, message, prefix + ".socks5ProxyEndpoint", index));
            }

            var hasUsername = !string.IsNullOrEmpty(rule.Username);
            var hasPassword = !string.IsNullOrEmpty(rule.Password);
            if (hasUsername && !hasPassword)
            {
                issues.Add(Error(
                    "AUTH_PASSWORD_MISSING",
                    "配置用户名时必须填写密码。",
                    prefix + ".password",
                    index));
            }
            else if (!hasUsername && hasPassword)
            {
                issues.Add(Error(
                    "AUTH_USERNAME_MISSING",
                    "配置密码时必须填写用户名。",
                    prefix + ".username",
                    index));
            }

            ValidateCredentialLength(
                rule.Username,
                "AUTH_USERNAME_TOO_LONG",
                "SOCKS5 身份验证的用户名编码后不得超过 255 字节。",
                prefix + ".username",
                index,
                issues);
            ValidateCredentialLength(
                rule.Password,
                "AUTH_PASSWORD_TOO_LONG",
                "SOCKS5 身份验证的密码编码后不得超过 255 字节。",
                prefix + ".password",
                index,
                issues);

            ValidateApplications(rule.AppNames, prefix, index, issues);
            ValidateProtocols(rule.SupportedProtocols, prefix, index, issues);
            ValidateAddressFamilies(rule.SupportedAddressFamilies, prefix, index, issues);
            ValidateTransportAndTls(rule, prefix, index, issues);
        }

        private static void ValidateCredentialLength(
            string value,
            string code,
            string message,
            string path,
            int? ruleIndex,
            ICollection<ValidationIssue> issues)
        {
            if (string.IsNullOrEmpty(value))
                return;

            // These arguments match msclr::interop::marshal_as<std::string>, so the
            // service validates the bytes its unmanaged SOCKS5 layer will receive.
            var encodedByteCount = WideCharToMultiByte(
                ThreadAnsiCodePage,
                NoBestFitCharacters,
                value,
                value.Length,
                IntPtr.Zero,
                0,
                IntPtr.Zero,
                IntPtr.Zero);
            if (encodedByteCount == 0 || encodedByteCount > Socks5CredentialMaximumEncodedBytes)
                issues.Add(Error(code, message, path, ruleIndex));
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern int WideCharToMultiByte(
            uint codePage,
            uint flags,
            string wideCharacters,
            int wideCharacterCount,
            IntPtr multiByteCharacters,
            int multiByteCharacterCapacity,
            IntPtr defaultCharacter,
            IntPtr usedDefaultCharacter);

        private static void ValidateApplications(
            IList<string> appNames,
            string prefix,
            int? ruleIndex,
            ICollection<ValidationIssue> issues)
        {
            if (appNames == null || appNames.Count == 0)
            {
                issues.Add(Warning(
                    "APP_NAMES_EMPTY",
                    "此规则没有应用程序匹配项，不会匹配任何进程。",
                    prefix + ".appNames",
                    ruleIndex));
                return;
            }

            var meaningfulCount = 0;
            var catchAllCount = 0;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < appNames.Count; index++)
            {
                var appName = appNames[index];
                if (appName == string.Empty)
                {
                    meaningfulCount++;
                    catchAllCount++;
                    continue;
                }
                if (string.IsNullOrWhiteSpace(appName))
                {
                    issues.Add(Warning(
                        "APP_NAME_WHITESPACE",
                        "空值和仅包含空白的应用程序匹配项会被忽略。兜底规则是显式空字符串，而不是空格。",
                        prefix + ".appNames[" + index + "]",
                        ruleIndex));
                    continue;
                }

                meaningfulCount++;
                if (!seen.Add(appName))
                {
                    issues.Add(Warning(
                        "APP_MATCH_DUPLICATE",
                        "此应用程序匹配项在同一规则中重复。",
                        prefix + ".appNames[" + index + "]",
                        ruleIndex));
                }
            }

            if (meaningfulCount == 0)
            {
                issues.Add(Warning(
                    "APP_NAMES_EMPTY",
                    "移除空白条目后，此规则没有应用程序匹配项。",
                    prefix + ".appNames",
                    ruleIndex));
            }
            if (catchAllCount > 1)
            {
                issues.Add(Warning(
                    "CATCH_ALL_DUPLICATE_IN_RULE",
                    "此规则包含多个兜底匹配项；只需一个显式空字符串。",
                    prefix + ".appNames",
                    ruleIndex));
            }
        }

        private static void ValidateProtocols(
            IList<string> protocols,
            string prefix,
            int? ruleIndex,
            ICollection<ValidationIssue> issues)
        {
            if (protocols == null)
                return; // Omitted means both for backward compatibility.
            if (protocols.Count == 0)
            {
                issues.Add(Warning(
                    "PROTOCOLS_EMPTY_LEGACY",
                    "为了兼容，显式的空协议列表会被视为 TCP 和 UDP。新编辑的规则应至少选择一种协议。",
                    prefix + ".supportedProtocols",
                    ruleIndex));
                return;
            }

            for (var index = 0; index < protocols.Count; index++)
            {
                var value = protocols[index];
                if (!string.Equals(value, "TCP", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(value, "UDP", StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add(Error(
                        "PROTOCOL_UNKNOWN",
                        "只有 TCP 和 UDP 是有效的协议值；未知值不得静默启用两种协议。",
                        prefix + ".supportedProtocols[" + index + "]",
                        ruleIndex));
                }
            }
        }

        private static void ValidateAddressFamilies(
            IList<string> families,
            string prefix,
            int? ruleIndex,
            ICollection<ValidationIssue> issues)
        {
            if (families == null)
                return; // Omitted means both.
            if (families.Count == 0)
            {
                issues.Add(Error(
                    "ADDRESS_FAMILIES_EMPTY",
                    "请选择 IPv4、IPv6 或两者都选；省略该属性会保留旧版双地址族默认值。",
                    prefix + ".supportedAddressFamilies",
                    ruleIndex));
                return;
            }

            for (var index = 0; index < families.Count; index++)
            {
                var value = families[index];
                if (!string.Equals(value, "IPv4", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(value, "IPv6", StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add(Error(
                        "ADDRESS_FAMILY_UNKNOWN",
                        "只有 IPv4 和 IPv6 是有效的地址族值。",
                        prefix + ".supportedAddressFamilies[" + index + "]",
                        ruleIndex));
                }
            }
        }

        private static void ValidateTransportAndTls(
            ProxyRule rule,
            string prefix,
            int? ruleIndex,
            ICollection<ValidationIssue> issues)
        {
            string transport;
            if (!ConfigurationNormalizer.TryGetCanonicalTransport(rule.Socks5Transport, out transport))
            {
                issues.Add(Error(
                    "TRANSPORT_UNKNOWN",
                    "SOCKS5 传输必须是 TCP 或 TLS。",
                    prefix + ".socks5Transport",
                    ruleIndex));
                return;
            }

            if (transport != "TLS")
                return;

            if (!string.IsNullOrWhiteSpace(rule.TlsPinnedSha256))
            {
                var fingerprint = ConfigurationNormalizer.NormalizeFingerprint(rule.TlsPinnedSha256);
                if (fingerprint.Length != 64 || !fingerprint.All(Uri.IsHexDigit))
                {
                    issues.Add(Error(
                        "TLS_PIN_INVALID",
                        "移除分隔符后，证书固定值必须恰好包含 64 个十六进制 SHA-256 字符。",
                        prefix + ".tlsPinnedSha256",
                        ruleIndex));
                }
            }

            if (rule.TlsAllowInvalidCertificate)
            {
                var hasPin = !string.IsNullOrWhiteSpace(rule.TlsPinnedSha256);
                issues.Add(Warning(
                    hasPin ? "TLS_INVALID_CERTIFICATE_ALLOWED" : "TLS_IDENTITY_VERIFICATION_DISABLED",
                    hasPin
                        ? "已禁用正常 TLS 证书验证。已配置的证书固定值仍是身份检查依据。"
                        : "允许无效 TLS 证书且未配置固定值。上游身份验证已完全禁用。",
                    prefix + ".tlsAllowInvalidCertificate",
                    ruleIndex));
            }
        }

        private static void ValidateCatchAllOrdering(IList<ProxyRule> rules, ICollection<ValidationIssue> issues)
        {
            var catchAllRules = new List<int>();
            for (var index = 0; index < rules.Count; index++)
            {
                var rule = rules[index];
                if (rule != null && rule.AppNames != null && rule.AppNames.Contains(string.Empty))
                {
                    catchAllRules.Add(index);
                    if (index < rules.Count - 1)
                    {
                        issues.Add(Warning(
                            "CATCH_ALL_NOT_LAST",
                            "此兜底规则会屏蔽其后的所有规则，因为首个匹配的代理规则生效。请将其移到最后。",
                            RulePath(index) + ".appNames",
                            index));
                    }
                }
            }

            if (catchAllRules.Count > 1)
            {
                foreach (var index in catchAllRules.Skip(1))
                {
                    issues.Add(Warning(
                        "MULTIPLE_CATCH_ALL_RULES",
                        "配置了多个兜底规则；只有第一个有效的兜底规则可被命中。",
                        RulePath(index) + ".appNames",
                        index));
                }
            }
        }

        private static void ValidateApplicationOverlap(IList<ProxyRule> rules, ICollection<ValidationIssue> issues)
        {
            var previous = new List<ApplicationMatch>();
            for (var ruleIndex = 0; ruleIndex < rules.Count; ruleIndex++)
            {
                var rule = rules[ruleIndex];
                if (rule == null || rule.AppNames == null)
                    continue;

                var currentRuleMatches = new List<ApplicationMatch>();

                for (var appIndex = 0; appIndex < rule.AppNames.Count; appIndex++)
                {
                    var value = rule.AppNames[appIndex];
                    if (string.IsNullOrWhiteSpace(value))
                        continue;

                    var shadowingMatch = previous.FirstOrDefault(item => MatchesOverlap(item.Value, value));
                    if (shadowingMatch != null)
                    {
                        issues.Add(Warning(
                            "APP_MATCH_SHADOWED",
                            "此应用程序匹配项与规则 " +
                            (shadowingMatch.RuleIndex + 1) + " 中的较早匹配项重叠；较早的规则生效。",
                            RulePath(ruleIndex) + ".appNames[" + appIndex + "]",
                            ruleIndex));
                    }

                    currentRuleMatches.Add(new ApplicationMatch(ruleIndex, value));
                }

                // Duplicate matches within one rule are diagnosed separately by
                // ValidateApplications. Only prior rules can win because of ordering.
                previous.AddRange(currentRuleMatches);
            }
        }

        private static bool MatchesOverlap(string left, string right)
        {
            if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
                return true;

            var leftIsPath = left.IndexOf('/') >= 0 || left.IndexOf('\\') >= 0;
            var rightIsPath = right.IndexOf('/') >= 0 || right.IndexOf('\\') >= 0;
            if (leftIsPath && rightIsPath)
            {
                return left.IndexOf(right, StringComparison.OrdinalIgnoreCase) >= 0 ||
                       right.IndexOf(left, StringComparison.OrdinalIgnoreCase) >= 0;
            }
            if (leftIsPath || rightIsPath)
            {
                var pathValue = leftIsPath ? left : right;
                var nameValue = leftIsPath ? right : left;
                var pathTail = GetPathTail(pathValue);
                return !string.IsNullOrEmpty(pathTail) && NameMatchesOverlap(pathTail, nameValue);
            }

            return NameMatchesOverlap(left, right);
        }

        private static bool NameMatchesOverlap(string left, string right)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase) ||
                   left.StartsWith(right + ".", StringComparison.OrdinalIgnoreCase) ||
                   right.StartsWith(left + ".", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetPathTail(string value)
        {
            var lastSlash = Math.Max(value.LastIndexOf('/'), value.LastIndexOf('\\'));
            return lastSlash >= 0 && lastSlash < value.Length - 1
                ? value.Substring(lastSlash + 1)
                : lastSlash < 0 ? value : string.Empty;
        }

        private static string RulePath(int index)
        {
            return "proxies[" + index + "]";
        }

        private static ValidationIssue Error(string code, string message, string path, int? ruleIndex = null)
        {
            return new ValidationIssue(ValidationSeverity.Error, code, message, path, ruleIndex);
        }

        private static ValidationIssue Warning(string code, string message, string path, int? ruleIndex = null)
        {
            return new ValidationIssue(ValidationSeverity.Warning, code, message, path, ruleIndex);
        }

        private sealed class ApplicationMatch
        {
            public ApplicationMatch(int ruleIndex, string value)
            {
                RuleIndex = ruleIndex;
                Value = value;
            }

            public int RuleIndex { get; }

            public string Value { get; }
        }
    }
}
