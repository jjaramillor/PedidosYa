using Delivery.BuildingBlocks;
using Delivery.Contracts;
using Npgsql;
using static Delivery.BuildingBlocks.ModuleSql;
namespace Delivery.Modules.Support;
public sealed record CaseInput(Guid OrderId,string Subject,string Description,Guid[] EvidenceIds);
public sealed record CaseAction(string State,string Resolution,Guid? AssignedTo);
public sealed record SupportCase(Guid Id,Guid OrderId,Guid OpenedBy,Guid? AssignedTo,string Subject,string State,DateTimeOffset DueAt,bool Overdue);
public sealed class SupportService(NpgsqlDataSource source,IOrderDirectory orders,IMerchantDirectory merchants,IDeliveryDirectory deliveries,IDocumentDirectory documents,IIdentityDirectory identities):IModuleMigration
{
 public Task ApplyAsync(CancellationToken ct)=>MigrateAsync(source,typeof(SupportService).Assembly,"support-001","Delivery.Modules.Support.Infrastructure.001.sql",ct);
 public async Task<bool> CaseEvidenceAsync(Guid document,CancellationToken ct)
 {
  await using var db=await source.OpenConnectionAsync(ct);await using var query=Command(db,null,"SELECT EXISTS(SELECT 1 FROM support.evidence WHERE document_id=@id)",("id",document));
  return await query.ExecuteScalarAsync(ct) is true;
 }
 public async Task<Guid> OpenAsync(Caller caller,CaseInput request,CancellationToken ct)
 {
  var subject=Guard.Text(request.Subject,"subject");var description=Guard.Text(request.Description,"description",3000);
  Guard.Require(request.EvidenceIds is {Length:<=20},"invalid_evidence","Evidencias inválidas.");
  var order=await orders.GetAsync(request.OrderId,ct);
  Guard.Require(caller.Can("support.manage")||order.CustomerId==caller.Id||await merchants.CanOperateAsync(caller.Id,order.BranchId,ct)||await deliveries.CanReadAsync(caller.Id,order.Id,ct),"support_access_denied","Pedido no autorizado.",403);
  foreach(var evidence in request.EvidenceIds)Guard.Require(await documents.OwnsAsync(caller.Id,evidence,ct),"invalid_evidence","Una evidencia no te pertenece.");
  var id=Guid.CreateVersion7();await using var db=await source.OpenConnectionAsync(ct);await using var tx=await db.BeginTransactionAsync(ct);
  await ExecuteAsync(db,tx,"INSERT INTO support.cases(id,order_id,opened_by,subject,description,state,due_at) VALUES (@id,@order,@actor,@subject,@description,'open',now()+interval '30 minutes')",ct,("id",id),("order",order.Id),("actor",caller.Id),("subject",subject),("description",description));
  foreach(var evidence in request.EvidenceIds)await ExecuteAsync(db,tx,"INSERT INTO support.evidence(case_id,document_id,uploaded_by) VALUES (@case,@file,@actor)",ct,("case",id),("file",evidence),("actor",caller.Id));
  await AuditAsync(db,tx,caller,"support",id,"case.opened","support request",ct);await tx.CommitAsync(ct);return id;
 }
 private async Task<SupportCase> ReadAsync(Caller caller,Guid id,NpgsqlConnection db,NpgsqlTransaction? tx,CancellationToken ct)
 {
  await using var query=Command(db,tx,"SELECT id,order_id,opened_by,assigned_to,subject,state,due_at FROM support.cases WHERE id=@id AND (@operator OR opened_by=@actor)",("id",id),("operator",caller.Can("support.manage")),("actor",caller.Id));
  await using var reader=await query.ExecuteReaderAsync(ct);if(!await reader.ReadAsync(ct))throw new ApiException(404,"case_not_found","Caso inexistente.");
  var due=new DateTimeOffset(reader.GetDateTime(6));return new(reader.GetGuid(0),reader.GetGuid(1),reader.GetGuid(2),reader.IsDBNull(3)?null:reader.GetGuid(3),reader.GetString(4),reader.GetString(5),due,due<DateTimeOffset.UtcNow&&reader.GetString(5) is "open" or "investigating");
 }
 public async Task<SupportCase[]> ListAsync(Caller caller,CancellationToken ct)
 {
  await using var db=await source.OpenConnectionAsync(ct);await using var query=Command(db,null,"SELECT id FROM support.cases WHERE @operator OR opened_by=@actor ORDER BY created_at DESC LIMIT 200",("operator",caller.Can("support.manage")),("actor",caller.Id));var ids=new List<Guid>();await using(var reader=await query.ExecuteReaderAsync(ct))while(await reader.ReadAsync(ct))ids.Add(reader.GetGuid(0));
  var result=new List<SupportCase>();foreach(var id in ids)result.Add(await ReadAsync(caller,id,db,null,ct));return result.ToArray();
 }
 public async Task<object> DetailsAsync(Caller caller,Guid id,CancellationToken ct)
 {
  await using var db=await source.OpenConnectionAsync(ct);var item=await ReadAsync(caller,id,db,null,ct);var notes=new List<object>();var evidence=new List<Guid>();
  string description;string? resolution;
  await using(var query=Command(db,null,"SELECT description,resolution FROM support.cases WHERE id=@id",("id",id)))
  {await using var reader=await query.ExecuteReaderAsync(ct);await reader.ReadAsync(ct);description=reader.GetString(0);resolution=reader.IsDBNull(1)?null:reader.GetString(1);}
  await using(var query=Command(db,null,"SELECT document_id FROM support.evidence WHERE case_id=@id",("id",id)))await using(var reader=await query.ExecuteReaderAsync(ct))while(await reader.ReadAsync(ct))evidence.Add(reader.GetGuid(0));
  await using(var query=Command(db,null,"SELECT actor_id,message,created_at FROM support.notes WHERE case_id=@id ORDER BY created_at",("id",id)))await using(var reader=await query.ExecuteReaderAsync(ct))while(await reader.ReadAsync(ct))notes.Add(new{Actor=reader.GetGuid(0),Message=reader.GetString(1),At=reader.GetDateTime(2)});
  return new{Case=item,Order=await orders.GetAsync(item.OrderId,ct),Description=description,Resolution=resolution,EvidenceIds=evidence,Notes=notes};
 }
 public async Task NoteAsync(Caller caller,Guid id,string message,CancellationToken ct)
 {
  message=Guard.Text(message,"message",3000);await using var db=await source.OpenConnectionAsync(ct);await using var tx=await db.BeginTransactionAsync(ct);var item=await ReadAsync(caller,id,db,tx,ct);
  Guard.Require(item.State is "open" or "investigating","case_closed","El caso está cerrado.",409);
  await ExecuteAsync(db,tx,"INSERT INTO support.notes(id,case_id,actor_id,message) VALUES (@note,@case,@actor,@message)",ct,("note",Guid.CreateVersion7()),("case",id),("actor",caller.Id),("message",message));await AuditAsync(db,tx,caller,"support",id,"case.noted","note",ct);await tx.CommitAsync(ct);
 }
 public async Task UpdateAsync(Caller caller,Guid id,CaseAction request,CancellationToken ct)
 {
  caller.RequirePermission("support.manage");var resolution=Guard.Text(request.Resolution,"resolution",3000);
  Guard.Require(request.State is "open" or "investigating" or "resolved" or "closed","invalid_case_state","Estado inválido.");
  if(request.AssignedTo is Guid user)Guard.Require(await identities.ActiveAsync(user,ct),"inactive_assignee","El operador no está activo.");
  await using var db=await source.OpenConnectionAsync(ct);await using var tx=await db.BeginTransactionAsync(ct);await GateAsync(db,tx,"case:"+id,ct);var item=await ReadAsync(caller,id,db,tx,ct);
  Guard.Require(item.State!="closed"||request.State=="closed","case_closed","No se puede reabrir un caso cerrado.",409);
  await ExecuteAsync(db,tx,"UPDATE support.cases SET state=@state,resolution=@resolution,assigned_to=@assignee WHERE id=@id",ct,("state",request.State),("resolution",resolution),("assignee",request.AssignedTo),("id",id));
  await AuditAsync(db,tx,caller,"support",id,"case.updated","operator resolution",ct);await tx.CommitAsync(ct);
 }
}

