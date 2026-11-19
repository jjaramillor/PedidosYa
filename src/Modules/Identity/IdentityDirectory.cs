using Delivery.Contracts;
using Npgsql;
namespace Delivery.Modules.Identity;
public sealed class IdentityDirectory(NpgsqlDataSource source):IIdentityDirectory
{
 public async Task<bool> ActiveAsync(Guid user,CancellationToken ct)=>await EmailAsync(user,ct) is not null;
 public async Task<string?> EmailAsync(Guid user,CancellationToken ct)
 {
  await using var connection=await source.OpenConnectionAsync(ct);await using var command=new NpgsqlCommand("SELECT email FROM identity.users WHERE id=@id AND status='active'",connection);command.Parameters.AddWithValue("id",user);return await command.ExecuteScalarAsync(ct) as string;
 }
 public async Task<Guid?> ActiveByEmailAsync(string email,CancellationToken ct)
 {
  var normalized=Delivery.BuildingBlocks.Guard.Text(email,"email",254).ToLowerInvariant();
  await using var connection=await source.OpenConnectionAsync(ct);await using var command=new NpgsqlCommand("SELECT id FROM identity.users WHERE email=@email AND status='active'",connection);command.Parameters.AddWithValue("email",normalized);
  return await command.ExecuteScalarAsync(ct) is Guid id?id:null;
 }
}

