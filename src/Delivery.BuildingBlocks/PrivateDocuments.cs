using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Npgsql;
using static Delivery.BuildingBlocks.ModuleSql;
namespace Delivery.BuildingBlocks;
public sealed class DocumentOptions {public string Root{get;set;}="uploads";}
public sealed record StoredFile(Guid Id,string Name,string ContentType,long Length,Guid OwnerId);
public sealed class PrivateDocumentStore(NpgsqlDataSource source,IOptions<DocumentOptions> options)
{
 public async Task<StoredFile> UploadAsync(Caller caller,IFormFile file,CancellationToken ct)
 {
  Guard.Require(file.Length is >0 and <=5242880&&file.ContentType is "application/pdf" or "image/jpeg" or "image/png","invalid_document","PDF, JPG o PNG de hasta 5 MB.");
  var id=Guid.CreateVersion7();var name=Path.GetFileName(file.FileName);name=Guard.Text(name,"fileName",200);
  var root=Path.GetFullPath(options.Value.Root);Directory.CreateDirectory(root);var path=Path.Combine(root,id.ToString("N"));
  await using var sourceStream=file.OpenReadStream();var prefix=new byte[8];var read=await sourceStream.ReadAsync(prefix,ct);
  var type=file.ContentType;
  Guard.Require(type switch{"application/pdf"=>read>=5&&System.Text.Encoding.ASCII.GetString(prefix,0,5)=="%PDF-","image/png"=>read==8&&prefix.SequenceEqual(new byte[]{137,80,78,71,13,10,26,10}),"image/jpeg"=>read>=3&&prefix[0]==255&&prefix[1]==216&&prefix[2]==255,_=>false},"invalid_document_content","El archivo no corresponde al formato declarado.");
  try
  {
   await using(var target=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None,81920,true))
   {
    await target.WriteAsync(prefix.AsMemory(0,read),ct);long copied=read;var buffer=new byte[81920];int count;
    while((count=await sourceStream.ReadAsync(buffer,ct))>0){copied+=count;Guard.Require(copied<=5242880,"document_too_large","Archivo demasiado grande.");await target.WriteAsync(buffer.AsMemory(0,count),ct);}
    Guard.Require(copied==file.Length,"invalid_document_length","Tamaño de documento inválido.");
   }
   await using var db=await source.OpenConnectionAsync(ct);
   await ExecuteAsync(db,null,"INSERT INTO platform.documents(id,owner_id,name,content_type,length,allowed_users) VALUES (@id,@owner,@name,@type,@length,@users)",ct,("id",id),("owner",caller.Id),("name",name),("type",type),("length",file.Length),("users",new[]{caller.Id}));
   return new(id,name,type,file.Length,caller.Id);
  }
  catch{if(File.Exists(path))File.Delete(path);throw;}
 }
 public async Task<StoredFile> GetAsync(Caller caller,Guid id,CancellationToken ct,bool authorizedCaseReview=false)
 {
  await using var db=await source.OpenConnectionAsync(ct);await using var query=Command(db,null,"SELECT id,name,content_type,length,owner_id FROM platform.documents WHERE id=@id AND (@admin OR @user=ANY(allowed_users))",("id",id),("admin",caller.Can("participants.manage")||(caller.Can("support.manage")&&authorizedCaseReview)),("user",caller.Id));
  await using var reader=await query.ExecuteReaderAsync(ct);if(!await reader.ReadAsync(ct))throw new ApiException(404,"document_not_found","Documento inexistente.");return new(reader.GetGuid(0),reader.GetString(1),reader.GetString(2),reader.GetInt64(3),reader.GetGuid(4));
 }
 public string PathFor(Guid id)=>Path.Combine(Path.GetFullPath(options.Value.Root),id.ToString("N"));
 public async Task<bool> OwnsAsync(Guid user,Guid id,CancellationToken ct)
 {
  await using var db=await source.OpenConnectionAsync(ct);await using var query=Command(db,null,"SELECT EXISTS(SELECT 1 FROM platform.documents WHERE id=@id AND owner_id=@user)",("id",id),("user",user));return await query.ExecuteScalarAsync(ct) is true;
 }
 public async Task GrantAsync(Guid owner,Guid id,Guid[] recipients,CancellationToken ct)
 {
  await using var db=await source.OpenConnectionAsync(ct);await ExecuteAsync(db,null,"UPDATE platform.documents SET allowed_users=ARRAY(SELECT DISTINCT unnest(allowed_users||@users::uuid[])) WHERE id=@id AND owner_id=@owner",ct,("users",recipients),("id",id),("owner",owner));
 }
 public async Task<bool> OwnsImageAsync(Guid user,Guid id,CancellationToken ct)
 {
  await using var db=await source.OpenConnectionAsync(ct);
  await using var query=Command(db,null,"SELECT EXISTS(SELECT 1 FROM platform.documents WHERE id=@id AND owner_id=@user AND content_type IN ('image/jpeg','image/png'))",("id",id),("user",user));
  return await query.ExecuteScalarAsync(ct) is true;
 }
}

