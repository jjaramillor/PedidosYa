namespace Delivery.Contracts;
public sealed record DocumentInfo(Guid Id,string Name,string ContentType,long Length);
public interface IDocumentDirectory
{
 Task<bool> OwnsAsync(Guid user,Guid id,CancellationToken ct);
 Task<bool> OwnsImageAsync(Guid user,Guid id,CancellationToken ct);
 Task GrantAsync(Guid owner,Guid id,Guid[] users,CancellationToken ct);
}

