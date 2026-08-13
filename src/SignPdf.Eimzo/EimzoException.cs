namespace SignPdf.Eimzo;

public class EimzoException : Exception
{
    public EimzoException(string message) : base(message)
    {
    }

    public EimzoException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class EimzoNotRunningException : EimzoException
{
    public EimzoNotRunningException()
        : base("E-IMZO не запущен. Установите клиент с e-imzo.uz, запустите E-IMZO.exe и повторите попытку.")
    {
    }

    public EimzoNotRunningException(Exception innerException)
        : base("E-IMZO не запущен. Установите клиент с e-imzo.uz, запустите E-IMZO.exe и повторите попытку.", innerException)
    {
    }
}
