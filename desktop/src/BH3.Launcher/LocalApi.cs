using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BH3.Launcher;

internal static class DispatchCrypto
{
    internal const string Key91 = "2c5501bee7f86edc308352a86b63df31";
    internal static byte[] Key(string version) => Encoding.ASCII.GetBytes(version.Split('_')[0] switch
    {
        "9.0.0" => "58260189ad8b4ab54d3dde91cb3b24d3",
        "9.1.0" => Key91,
        _ => throw new ArgumentException("暂不支持客户端版本：" + version)
    });
    internal static string Encrypt(string version, object payload)
    {
        using var aes = Aes.Create(); aes.Key = Key(version);
        return Convert.ToBase64String(aes.EncryptEcb(JsonSerializer.SerializeToUtf8Bytes(payload), PaddingMode.PKCS7));
    }
    internal static byte[] Decrypt(string version, byte[] ciphertext)
    { using var aes = Aes.Create(); aes.Key = Key(version); return aes.DecryptEcb(ciphertext, PaddingMode.PKCS7); }
}
internal sealed record ApiResponse(int Status, byte[] Body, string Type = "application/json; charset=utf-8");
internal sealed class LocalApi(LauncherConfig config)
{
    private const string Base = "https://outer-dp-pc01.bh3.com";
    // Sandbox profile only: 9.1's SDK reads AccountModel.realname before local purchases.
    // is_adult and dispatch's legacy-dialog flag do not populate that field.
    private const string LocalTestName = "本地测试";
    // AccountPlatNative 0x174560 parses realname_info.required independently of account.realname.
    private static object LocalRealnameInfo => new { required = false, action_type = "", action_ticket = "" };
    private string token => BH3.Protocol.LocalTicket.Issue(config.LocalAuthKey, uint.Parse(config.AccountUid, System.Globalization.CultureInfo.InvariantCulture), config.AccountName, DateTimeOffset.UtcNow);
    internal static ApiResponse Json(object value, int status = 200) => new(status, JsonSerializer.SerializeToUtf8Bytes(value));
    private static object Ok(object? data = null) => new { retcode = 0, success = true, message = "OK", data };
    internal ApiResponse Handle(WireRequest request)
    {
        var uri = new Uri("http://localhost" + request.Path); string path = uri.AbsolutePath;
        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Split('=', 2)).ToDictionary(x => Uri.UnescapeDataString(x[0]), x => x.Length > 1 ? Uri.UnescapeDataString(x[1]) : "", StringComparer.OrdinalIgnoreCase);
        if (request.Method == "OPTIONS") return new(204, []);
        if (request.Method is "GET" or "HEAD")
        {
            if (path == "/health") return Json(new { status = "ready", version = "9.1.0", game_server = config.ServerExe.Length > 0 ? "external" : "handshake-only" });
            if (path is "/query_dispatch" or "/query_gameserver" or "/query_gateway")
            {
                string version = query.GetValueOrDefault("version", "9.1.0"); object payload;
                if (path == "/query_dispatch")
                    payload = new { retcode = 0, region_list = new[] { new { dispatch_url = Base + "/query_gameserver", ext = new Dictionary<string, string> { ["1794460900"] = "Recommend" }, name = "pc01", retcode = 0, title = "本地服" } } };
                else
                {
                    // The retained capture contains a 9.1.0.6 manifest despite its historical filename.
                    var node = JsonNode.Parse(AppPaths.Resource("official_gameserver_90.json"))!.AsObject();
                    node["retcode"] = 0; node["msg"] = ""; node["is_data_ready"] = true; node["force_update_url"] = "";
                    node["server_cur_time"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(); node["server_cur_timezone"] = 8; node["region_name"] = "pc01";
                    // Encrypted 9.1 dispatch renames ext["98533854"] to ext.is_xxxx
                    // before reading DispatchSeverData +0x15a. server_ext is unrelated.
                    // This local environment flag omits the incompatible legacy binding UI;
                    // it does not change the SDK account's real-name verification state.
                    node["ext"]!.AsObject()["98533854"] = "1";
                    foreach (string name in new[] { "account_url", "account_url_backup", "oaserver_url" }) node[name] = Base;
                    foreach (string name in new[] { "gameserver", "gateway" }) node[name] = JsonSerializer.SerializeToNode(new { ip = "127.0.0.1", is_kcp = true, port = config.GamePort });
                    payload = node;
                }
                try { return new(200, Encoding.ASCII.GetBytes(DispatchCrypto.Encrypt(version, payload)), "text/plain; charset=utf-8"); }
                catch (ArgumentException) { return Json(new { retcode = -1, msg = "unsupported client version" }, 400); }
            }
            if (path == "/query_security_file")
            {
                string key = query.GetValueOrDefault("file_key", "9.1.0_gf_pc");
                return key == "9.1.0_gf_pc" ? new(200, AppPaths.Resource("security_file_9.1.0_gf_pc.json")) : Json(new { retcode = -1, msg = "security file not available" }, 404);
            }
            if (path.EndsWith("/mdk/shield/api/loadConfig")) return Json(Ok(new { id = 16, game_key = path.Contains("bh3_usa") ? "bh3_usa" : "bh3_cn", client = "PC", identity = "I_IDENTITY", guest = false, ignore_versions = "", scene = "S_NORMAL", name = "BH3Local", disable_regist = false, enable_email_captcha = false, thirdparty = Array.Empty<object>(), disable_mmt = true, server_guest = false }));
            if (path.EndsWith("/granter/api/getConfig") || path.EndsWith("/ma-passport/api/getConfig")) return Json(Ok(new { protocol = true, qr_enabled = false, log_level = "INFO", announce_url = Base + "/announcement/index.html", push_alias_type = 2, disable_ysdk_guard = true, enable_announce_popup = false, app_name = "BH3Local", enable_user_center = false }));
            if (path.EndsWith("/compareProtocolVersion")) return Json(Ok(new { modified = false }));
            if (path == "/combo/box/api/config/sdk/combo") return Json(Ok(new { vals = new { telemetry_config = "{\"dataupload_enable\":0}" } }));
            if (path.EndsWith("/sw/precache")) return Json(Ok(new { entries = Array.Empty<object>(), version = "0", enable = false }));
            if (path == "/device-fp/api/getExtList") return Json(Ok(new { code = 200, msg = "ok", ext_list = new[] { "deviceModel", "osVersion" }, pkg_list = Array.Empty<string>() }));
            if (path.EndsWith("-version.json")) return Json(new { version = 1 });
            // These are game SDK content responses, never a launcher UI or control endpoint.
            if (path.EndsWith("/announcement/index.html")) return new(200, AppPaths.Resource("announcement.html"), "text/html; charset=utf-8");
            if (path.EndsWith("/sw.html")) return new(200, "<!doctype html><title>BH3 Local</title>"u8.ToArray(), "text/html; charset=utf-8");
        }
        if (request.Method == "POST")
        {
            if (path.EndsWith("/compareProtocolVersion")) return Json(Ok(new { modified = false }));
            if (path.EndsWith("/mdk/shield/api/login") || path.EndsWith("/mdk/shield/api/verify"))
                return Json(Ok(new { account = new { uid = config.AccountUid, name = config.AccountName, token, email = "captain@local", is_email_verify = "1", country = "CN", realname = LocalTestName, identity_card = "LOCAL-TEST-ACCOUNT" }, device_grant_required = false, safe_mobile_required = false, realperson_required = false, reactivate_required = false, realname_info = LocalRealnameInfo, need_realperson = false }));
            if (path.EndsWith("/appLoginByPassword") || path.EndsWith("/loginByPassword") || path.EndsWith("/token/getByGameToken") || path.EndsWith("/getTokenByGameToken")) return Json(Ok(new { token = new { token_type = 1, token }, user_info = new { aid = config.AccountUid, mid = "", account_name = config.AccountName, email = "captain@local", is_email_verify = 0, area_code = "**", country = "CN", is_adult = 1, realname = LocalTestName, identity_code = "LOCAL-TEST-ACCOUNT" }, ext_user_info = new { guardian_email = "", birth = "0" }, reactivate_action_ticket = "", bind_email_action_ticket = "", realname_info = LocalRealnameInfo, need_realperson = false }));
            if (path.EndsWith("/combo/granter/login/v2/login")) return Json(Ok(new { account_type = 1, data = "{\"guest\":false}", heartbeat = false, open_id = config.AccountUid, combo_token = token, realname_info = LocalRealnameInfo, need_realperson = false }));
            if (path.EndsWith("/logout")) return Json(Ok());
            if (path == "/device-fp/api/getFp") return Json(Ok(new { device_fp = "bh3-local-desktop", code = 0, msg = "ok" }));
            if (path.EndsWith("/experiment/list")) return Json(Ok(Array.Empty<object>()));
        }
        if (path.EndsWith("/dataUpload") || path.EndsWith("/log/batch") || path is "/_ts" or "/report") return Json(new { code = 0, message = "OK" });
        if (path.EndsWith("/combo/red_dot/list")) return Json(Ok(new { alert = false, remind = false, extra_remind = false, remind_text = "" }));
        return Json(new { retcode = -1, msg = "local endpoint not implemented" }, 404);
    }
}
