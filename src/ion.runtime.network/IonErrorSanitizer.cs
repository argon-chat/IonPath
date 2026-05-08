namespace ion.runtime.network;

using ion.runtime;

internal static class IonErrorSanitizer
{
    public static IonProtocolError Sanitize(Exception ex, bool detailedErrors)
    {
        if (ex is IonRequestException ionEx)
            return ionEx.Error;

        if (detailedErrors)
            return IonProtocolError.INTERNAL_ERROR(ex.ToString());

        return IonProtocolError.INTERNAL_ERROR("An internal error occurred.");
    }
}
