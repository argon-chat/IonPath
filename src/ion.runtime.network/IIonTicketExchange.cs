namespace ion.runtime.network;

public interface IIonTicketExchange
{
    Task<ReadOnlyMemory<byte>> OnExchangeCreateAsync(IIonCallContext callContext);
    Task<(IonProtocolError?, object? ticket)> OnExchangeTransactionAsync(ReadOnlyMemory<byte> exchangeToken);
    void OnTicketApply(object ticketObject);
}