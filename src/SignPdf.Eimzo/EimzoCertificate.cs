namespace SignPdf.Eimzo;

public sealed class EimzoCertificate
{
    public string Disk { get; init; } = "";
    public string Path { get; init; } = "";
    public string Name { get; init; } = "";
    public string Alias { get; init; } = "";
    public string CommonName { get; init; } = "";
    public string GivenName { get; init; } = "";
    public string Surname { get; init; } = "";
    public string Organization { get; init; } = "";
    public string Inn { get; init; } = "";
    public string Pinfl { get; init; } = "";
    public string SerialNumber { get; init; } = "";
    public string Email { get; init; } = "";
    public DateTime? ValidFrom { get; init; }
    public DateTime? ValidTo { get; init; }

    public string DisplayName
    {
        get
        {
            var who = PersonName.ToTitleCase(string.IsNullOrWhiteSpace(CommonName) ? Name : CommonName);
            var id = !string.IsNullOrWhiteSpace(Pinfl) ? Pinfl : Inn;
            var until = ValidTo?.ToString("dd.MM.yyyy");
            if (!string.IsNullOrWhiteSpace(id) && until is not null)
            {
                return $"{who}  ·  {id}  ·  до {until}";
            }

            if (until is not null)
            {
                return $"{who}  ·  до {until}";
            }

            return who;
        }
    }

    public string StampFullName =>
        PersonName.ToTitleCase(string.IsNullOrWhiteSpace(CommonName) ? Name : CommonName);

    public string StampIdLabel
    {
        get
        {
            var hasInn = !string.IsNullOrWhiteSpace(Inn);
            var hasPinfl = !string.IsNullOrWhiteSpace(Pinfl);
            if (hasInn && hasPinfl)
            {
                return "инн/пинфл";
            }

            return hasPinfl && !hasInn ? "пинфл" : "инн";
        }
    }

    public string StampIdValue
    {
        get
        {
            var inn = Inn.Trim();
            var pinfl = Pinfl.Trim();
            if (inn.Length > 0 && pinfl.Length > 0)
            {
                return inn + "/" + pinfl;
            }

            return inn.Length > 0 ? inn : pinfl;
        }
    }

    public bool IsExpired => ValidTo is { } to && to.ToUniversalTime() < DateTime.UtcNow;
}
