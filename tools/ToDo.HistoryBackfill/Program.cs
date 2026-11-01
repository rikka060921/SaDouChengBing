using System.Security.Cryptography;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using MySqlConnector;
using ToDo.Context;
using ToDo.Domain;

try
{
    var options = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
    for (var i=0;i<args.Length;i++)
    {
        if (!args[i].StartsWith("--",StringComparison.Ordinal)) throw new ArgumentException("参数须使用 --name value");
        options[args[i][2..]] = i+1<args.Length&&!args[i+1].StartsWith("--",StringComparison.Ordinal)?args[++i]:"true";
    }
    var root = Path.GetFullPath(options.GetValueOrDefault("app-root") ?? throw new ArgumentException("需要 --app-root"));
    var package = Path.GetFullPath(options.GetValueOrDefault("package") ?? throw new ArgumentException("需要 --package"));
    var environment = options.GetValueOrDefault("environment", "Production");
    var configBuilder = new ConfigurationBuilder().SetBasePath(root).AddJsonFile("appsettings.json",false).AddJsonFile($"appsettings.{environment}.json",true);
    if(environment=="Development") configBuilder.AddUserSecrets("4d7bba61-ed81-47e8-b60d-60d6024729cc");
    var config = configBuilder.AddEnvironmentVariables().Build();
    var connection = new MySqlConnectionStringBuilder(config.GetConnectionString("DefaultConnection") ?? throw new InvalidOperationException("缺少数据库配置"));
    if (connection.Server is not ("localhost" or "127.0.0.1" or "::1")) throw new InvalidOperationException("补录工具仅连接执行机器上的本地数据库，拒绝远程库");
    var manifest = JsonSerializer.Deserialize<ProjectHistoryBackfillManifest>(await File.ReadAllTextAsync(Path.Combine(package,"manifest.json")),ProjectDevelopmentLedger.JsonOptions)
        ?? throw new ArgumentException("清单为空");
    _=ProjectHistoryImport.Parse(JsonSerializer.Serialize(manifest.Timeline));
    foreach(var file in manifest.Files)
    {
        var path=ProjectHistoryBackfillService.ResolveBundlePath(package,file.Path);
        await using var input=File.OpenRead(path);
        if(!string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(input)),file.Hash,StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("文件哈希不符："+file.Name);
    }
    await using var db=new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseMySql(connection.ConnectionString,new MySqlServerVersion(new Version(8,0,30))).Options);
    if(options.ContainsKey("apply"))
    {
        await db.Database.OpenConnectionAsync();
        using var guard=db.Database.GetDbConnection().CreateCommand();
        guard.CommandText="SELECT GET_LOCK(@name,10)";
        var parameter=guard.CreateParameter();parameter.ParameterName="@name";parameter.Value="todo-history:"+Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(manifest.ProjectName)))[..32];guard.Parameters.Add(parameter);
        if(Convert.ToInt32(await guard.ExecuteScalarAsync())!=1)throw new InvalidOperationException("该项目正在补录，请稍后重试");
    }
    var actorName=options.GetValueOrDefault("actor",manifest.LeaderUserName);
    var actor=await db.Users.AsNoTracking().SingleOrDefaultAsync(u=>u.UserName==actorName&&!u.IsDeleted&&u.Status==ToDo.Entities.UserStatus.Active)
        ?? throw new InvalidOperationException("找不到有效补录操作人");
    var existing=await db.Project.AsNoTracking().Where(p=>p.Name==manifest.ProjectName&&!p.IsDeleted).Select(p=>new{p.Id,p.Name}).ToListAsync();
    if(existing.Count>1)throw new InvalidOperationException("存在多个同名正式项目，停止自动补录");
    var beforeTasks=existing.Count==1?await db.ToDoTasks.CountAsync(t=>t.ProjectId==existing[0].Id):0;
    var beforeMeetings=existing.Count==1?await db.MeetingMinutes.CountAsync(t=>t.ProjectId==existing[0].Id):0;
    Console.WriteLine(JsonSerializer.Serialize(new{Mode=options.ContainsKey("apply")?"apply":"preview",Server=connection.Server,connection.Port,connection.Database,
        Actor=actor.UserName,ExistingProjects=existing,ManifestFiles=manifest.Files.Count,Milestones=manifest.Timeline.Count,BeforeTasks=beforeTasks,BeforeMeetings=beforeMeetings}));
    if(options.TryGetValue("backup-output",out var backupOutput))
    {
        backupOutput=Path.GetFullPath(backupOutput);
        if(File.Exists(backupOutput))throw new ArgumentException("不覆盖已有备份，请使用新的备份目录");
        var dump=options.GetValueOrDefault("mysql-dump","mysqldump.exe");
        var start=new ProcessStartInfo(dump){UseShellExecute=false,RedirectStandardError=true,RedirectStandardOutput=true,CreateNoWindow=true};
        foreach(var value in new[]{"--host="+connection.Server,"--port="+connection.Port,"--user="+connection.UserID,"--single-transaction","--routines","--events","--triggers","--set-gtid-purged=OFF","--no-tablespaces","--default-character-set=utf8mb4","--result-file="+backupOutput,connection.Database})start.ArgumentList.Add(value);
        start.Environment["MYSQL_PWD"]=connection.Password;
        using var process=Process.Start(start)??throw new InvalidOperationException("无法启动数据库备份");
        var error=process.StandardError.ReadToEndAsync();var output=process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();await error;await output;
        if(process.ExitCode!=0||!File.Exists(backupOutput)||new FileInfo(backupOutput).Length<100)throw new InvalidOperationException("数据库备份失败，未开始补录；请核查备份工具和数据库权限");
        options["backup"]=backupOutput;Console.WriteLine("Database backup completed; credentials were not written to arguments or reports.");
    }
    if(!options.ContainsKey("apply"))return 0;
    var backup=options.GetValueOrDefault("backup")??throw new ArgumentException("正式补录必须指定已完成的数据库备份 --backup");
    if(!File.Exists(backup)||new FileInfo(backup).Length<100)throw new ArgumentException("备份不存在或为空，停止补录");
    var documents=new ProjectDocumentService(db,new BackfillEnvironment(root,environment));
    var result=await new ProjectHistoryBackfillService(db,documents).ApplyAsync(package,manifest,actor.Id);
    var current=await db.ProjectDocuments.AsNoTracking().Where(d=>d.ProjectId==result.ProjectId&&d.IsCurrent).ToListAsync();
    var invalidFiles=new List<string>();
    foreach(var doc in current)
    {
        var resolved=await documents.ResolveDownloadAsync(doc.Id,result.ProjectId);
        if(resolved==null){invalidFiles.Add(doc.FileName+":missing");continue;}
        await using var input=File.OpenRead(resolved.Value.FullPath);
        if(!string.IsNullOrEmpty(doc.FileHash)&&!string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(input)),doc.FileHash,StringComparison.OrdinalIgnoreCase))invalidFiles.Add(doc.FileName+":hash");
    }
    var ledger=await new ProjectDevelopmentLedgerReader(db,documents).ReadAsync(result.ProjectId,actor.Id);
    var report=new{Result=result,CurrentDocuments=current.Count,InvalidFiles=invalidFiles,GitChanges=ledger?.Ledger.Changes.Count,
        GitAuthors=ledger?.Ledger.Changes.Select(c=>c.Author).Distinct().Count(),Milestones=await db.ProjectHistoryEntries.CountAsync(h=>h.ProjectId==result.ProjectId),
        TasksUnchanged=beforeTasks==await db.ToDoTasks.CountAsync(t=>t.ProjectId==result.ProjectId),MeetingsUnchanged=beforeMeetings==await db.MeetingMinutes.CountAsync(m=>m.ProjectId==result.ProjectId)};
    var json=JsonSerializer.Serialize(report,ProjectDevelopmentLedger.JsonOptions);Console.WriteLine(json);
    if(options.TryGetValue("report",out var reportPath))await File.WriteAllTextAsync(Path.GetFullPath(reportPath),json);
    return invalidFiles.Count==0?0:2;
}
catch(Exception ex)
{
    // Never serialize configuration, connection strings, passwords or arbitrary exception details.
    Console.Error.WriteLine(ex is ArgumentException or InvalidOperationException or UnauthorizedAccessException ? ex.Message : $"补录失败（{ex.GetType().Name}）；未输出敏感连接信息，请核查本机配置和备份。");
    return 1;
}

sealed class BackfillEnvironment(string root,string name):IHostEnvironment
{
    public string EnvironmentName{get;set;}=name;
    public string ApplicationName{get;set;}="ToDo.HistoryBackfill";
    public string ContentRootPath{get;set;}=root;
    public IFileProvider ContentRootFileProvider{get;set;}=new PhysicalFileProvider(root);
}
