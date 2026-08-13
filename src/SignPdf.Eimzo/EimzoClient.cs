using System.Text.Json.Nodes;

namespace SignPdf.Eimzo;

public sealed class EimzoClient : IDisposable
{
    public const string InstallUrl = "https://e-imzo.uz";

    private static readonly string[] ApiKeys =
    {
        "localhost",
        "96D0C1491615C82B9A54D9989779DF825B690748224C2B04F500F370D51827CE2644D8D4A82C18184D73AB8530BB8ED537269603F61DB0D03D2104ABF789970B",
        "127.0.0.1",
        "A7BCFA5D490B351BE0754130DF03A068F855DB4333D43921125B9CF2670EF6A40370C646B90401955E1F7BC9CDBF59CE0B2C5467D820BE189C845D0B79CFC96F",
    };

    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan PasswordTimeout = TimeSpan.FromMinutes(6);
    private static readonly TimeSpan SignTimeout = TimeSpan.FromMinutes(8);

    private readonly CapiwsClient _capiws = new();
    private bool _apiKeysInstalled;
    private bool _trustStoreTried;
    private string _trustStoreId = "";

    public string? Endpoint => _capiws.WorkingUrl;
    public string? Version { get; private set; }

    public async Task<string> ConnectAsync(CancellationToken cancellationToken = default)
    {
        var response = await _capiws.CallAsync(new { name = "version" }, ShortTimeout, cancellationToken)
            .ConfigureAwait(false);
        EnsureSuccess(response, "Не удалось получить версию E-IMZO.");

        var major = ReadString(response, "major") ?? "?";
        var minor = ReadString(response, "minor") ?? "?";
        Version = $"{major}.{minor}";

        if (!_apiKeysInstalled)
        {
            var keyResponse = await _capiws.CallAsync(
                    new { name = "apikey", arguments = ApiKeys },
                    ShortTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
            EnsureSuccess(keyResponse, "E-IMZO отклонил API-ключ для localhost.");
            _apiKeysInstalled = true;
        }

        return Version;
    }

    public async Task<IReadOnlyList<EimzoCertificate>> ListCertificatesAsync(CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

        var response = await _capiws.CallAsync(
                new { plugin = "pfx", name = "list_all_certificates" },
                ShortTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        EnsureSuccess(response, "Не удалось получить список сертификатов.");

        var list = new List<EimzoCertificate>();
        if (response["certificates"] is JsonArray array)
        {
            foreach (var item in array)
            {
                AddCertificate(list, item as JsonObject);
            }
        }
        else if (response["certificates"] is JsonObject map)
        {
            foreach (var pair in map)
            {
                AddCertificate(list, pair.Value as JsonObject);
            }
        }

        return list;
    }

    public async Task<string> LoadKeyAsync(EimzoCertificate certificate, CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

        var load = await _capiws.CallAsync(
                new
                {
                    plugin = "pfx",
                    name = "load_key",
                    arguments = new[] { certificate.Disk, certificate.Path, certificate.Name, certificate.Alias },
                },
                PasswordTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        EnsureSuccess(load, "Не удалось загрузить ключ ЭЦП.");

        var keyId = ReadString(load, "keyId")
                    ?? throw new EimzoException("E-IMZO не вернул идентификатор ключа.");

        var verify = await _capiws.CallAsync(
                new { plugin = "pfx", name = "verify_password", arguments = new[] { keyId } },
                PasswordTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        EnsureSuccess(verify, "Пароль ключа ЭЦП не подтверждён.");

        return keyId;
    }

    public async Task<byte[]> CreateDetachedPkcs7Async(
        string keyId,
        byte[] data,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        ArgumentNullException.ThrowIfNull(data);
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

        var data64 = Convert.ToBase64String(data);
        var response = await _capiws.CallAsync(
                new
                {
                    plugin = "pkcs7",
                    name = "create_pkcs7",
                    arguments = new[] { data64, keyId, "yes" },
                },
                SignTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        EnsureSuccess(response, "E-IMZO не смог создать подпись PKCS#7.");

        var pkcs7 = ReadString(response, "pkcs7_64")
                    ?? throw new EimzoException("E-IMZO не вернул документ PKCS#7.");
        return Convert.FromBase64String(pkcs7);
    }

    public async Task<IReadOnlyList<byte[]>> GetCertificateChainAsync(
        string keyId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

        var response = await _capiws.CallAsync(
                new
                {
                    plugin = "x509",
                    name = "get_certificate_chain",
                    arguments = new[] { keyId },
                },
                ShortTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        if (!IsSuccess(response))
        {
            return Array.Empty<byte[]>();
        }

        var list = new List<byte[]>();
        if (response["certificates"] is JsonArray array)
        {
            foreach (var item in array)
            {
                var text = item?.ToString();
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                try
                {
                    list.Add(Convert.FromBase64String(text));
                }
                catch (FormatException)
                {
                    // skip
                }
            }
        }

        return list;
    }

    public async Task<EimzoPkcs7Info> GetDetachedPkcs7InfoAsync(
        byte[] data,
        byte[] pkcs7,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(pkcs7);
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

        var trustStoreId = await TryOpenTruststoreAsync(cancellationToken).ConfigureAwait(false);
        var data64 = Convert.ToBase64String(data);
        var pkcs764 = Convert.ToBase64String(TrimDer(pkcs7));

        // E-IMZO 5.00 регистрирует метод с 3 аргументами; без tsid приходит «Функция не найдена».
        JsonObject? response = null;
        if (!string.IsNullOrWhiteSpace(trustStoreId))
        {
            response = await CallDetachedInfoAsync(data64, pkcs764, trustStoreId, cancellationToken)
                .ConfigureAwait(false);
            if (IsSuccess(response))
            {
                return EimzoPkcs7Info.FromResponse(response);
            }
        }

        response = await CallDetachedInfoAsync(data64, pkcs764, "", cancellationToken)
            .ConfigureAwait(false);
        if (!IsSuccess(response))
        {
            return new EimzoPkcs7Info
            {
                Success = false,
                Reason = ReadString(response, "reason") ?? "E-IMZO не смог разобрать PKCS#7.",
                RawJson = response.ToJsonString(),
            };
        }

        return EimzoPkcs7Info.FromResponse(response);
    }

    private Task<JsonObject> CallDetachedInfoAsync(
        string data64,
        string pkcs764,
        string trustStoreId,
        CancellationToken cancellationToken)
    {
        return _capiws.CallAsync(
            new
            {
                plugin = "pkcs7",
                name = "get_pkcs7_detached_info",
                arguments = new[] { data64, pkcs764, trustStoreId },
            },
            SignTimeout,
            cancellationToken);
    }

    public async Task<bool> VerifyCertificateIssuedByAsync(
        byte[] subjectCertificate,
        byte[] issuerCertificate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subjectCertificate);
        ArgumentNullException.ThrowIfNull(issuerCertificate);
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

        var response = await _capiws.CallAsync(
                new
                {
                    plugin = "x509",
                    name = "verify_certificate",
                    arguments = new[]
                    {
                        Convert.ToBase64String(TrimDer(subjectCertificate)),
                        Convert.ToBase64String(TrimDer(issuerCertificate)),
                    },
                },
                ShortTimeout,
                cancellationToken)
            .ConfigureAwait(false);

        return IsSuccess(response);
    }

    public async Task<string> CallPluginJsonAsync(
        string plugin,
        string name,
        IReadOnlyList<string>? arguments = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        var payload = new Dictionary<string, object?> { ["name"] = name };
        if (!string.IsNullOrWhiteSpace(plugin))
        {
            payload["plugin"] = plugin;
        }

        if (arguments is not null)
        {
            payload["arguments"] = arguments;
        }

        var response = await _capiws.CallAsync(payload, ShortTimeout, cancellationToken).ConfigureAwait(false);
        return response.ToJsonString();
    }

    public async Task UnloadKeyAsync(string keyId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(keyId))
        {
            return;
        }

        try
        {
            await _capiws.CallAsync(
                    new { plugin = "pfx", name = "unload_key", arguments = new[] { keyId } },
                    ShortTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (EimzoException)
        {
            // Unload is best-effort; an expired session is not fatal.
        }
    }

    public void Dispose()
    {
        // CAPIWS uses a short-lived socket per call.
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_apiKeysInstalled && Version is not null)
        {
            return;
        }

        await ConnectAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> TryOpenTruststoreAsync(CancellationToken cancellationToken)
    {
        if (_trustStoreTried)
        {
            return _trustStoreId;
        }

        _trustStoreTried = true;
        EnsureHomeTruststore();
        try
        {
            var response = await _capiws.CallAsync(
                    new { plugin = "truststore-jks", name = "open_truststore" },
                    ShortTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
            if (IsSuccess(response))
            {
                _trustStoreId = ReadString(response, "tsId")
                                ?? ReadString(response, "tsid")
                                ?? ReadString(response, "id")
                                ?? ReadString(response, "trustStoreId")
                                ?? ReadString(response, "keyId")
                                ?? "";
            }
        }
        catch (EimzoException)
        {
            _trustStoreId = "";
        }

        return _trustStoreId;
    }

    private static void EnsureHomeTruststore()
    {
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(home))
            {
                return;
            }

            var dest = Path.Combine(home, "truststore.jks");
            if (File.Exists(dest))
            {
                return;
            }

            foreach (var folder in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     })
            {
                if (string.IsNullOrWhiteSpace(folder))
                {
                    continue;
                }

                var source = Path.Combine(folder, "E-IMZO", "truststore.jks");
                if (!File.Exists(source))
                {
                    continue;
                }

                File.Copy(source, dest, overwrite: false);
                return;
            }
        }
        catch
        {
            // truststore is optional; verification still uses x509.verify_certificate
        }
    }

    private static void AddCertificate(List<EimzoCertificate> list, JsonObject? item)
    {
        if (item is null)
        {
            return;
        }

        var disk = ReadString(item, "disk") ?? "";
        var path = ReadString(item, "path") ?? "";
        var name = ReadString(item, "name") ?? "";
        var alias = ReadString(item, "alias") ?? "";
        if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(alias))
        {
            return;
        }

        list.Add(X500AliasParser.Parse(disk, path, name, alias));
    }

    private static void EnsureSuccess(JsonObject response, string fallback)
    {
        if (IsSuccess(response))
        {
            return;
        }

        var reason = ReadString(response, "reason");
        throw new EimzoException(string.IsNullOrWhiteSpace(reason) ? fallback : reason);
    }

    internal static bool IsSuccess(JsonObject response)
    {
        return response["success"] is JsonValue success && success.TryGetValue<bool>(out var ok) && ok;
    }

    internal static string? ReadString(JsonObject obj, string name)
    {
        if (obj[name] is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<string>(out var text))
        {
            return text;
        }

        return value.ToString();
    }

    internal static byte[] TrimDer(byte[] data)
    {
        if (data.Length < 2 || data[0] != 0x30)
        {
            return data;
        }

        var offset = 1;
        var lenByte = data[offset++];
        int length;
        if ((lenByte & 0x80) == 0)
        {
            length = lenByte;
        }
        else
        {
            var count = lenByte & 0x7F;
            if (count == 0 || count > 4 || offset + count > data.Length)
            {
                return data;
            }

            length = 0;
            for (var i = 0; i < count; i++)
            {
                length = (length << 8) | data[offset++];
            }
        }

        var total = offset + length;
        if (total <= 0 || total > data.Length)
        {
            return data;
        }

        if (total == data.Length)
        {
            return data;
        }

        var trimmed = new byte[total];
        Buffer.BlockCopy(data, 0, trimmed, 0, total);
        return trimmed;
    }
}
