using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SchoolMessenger.Contracts;

namespace SchoolMessenger.Shared;

/// <summary>The same date-resolution rules are used by the school service and its external replica.</summary>
public sealed class TimetableData(Func<SqliteConnection> open)
{
    public static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    public static string Today=>DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(9)).ToString("yyyy-MM-dd");
    public void Initialize()=>Execute("""
        CREATE TABLE IF NOT EXISTS TimetableBatches(Revision INTEGER PRIMARY KEY AUTOINCREMENT,Id TEXT UNIQUE NOT NULL,Owner TEXT NOT NULL,
          ClientId TEXT NOT NULL,Payload TEXT NOT NULL,Delivered INTEGER NOT NULL DEFAULT 0,UNIQUE(Owner,ClientId));
        CREATE TABLE IF NOT EXISTS TimetableNotices(Revision INTEGER NOT NULL,UserId TEXT NOT NULL,Payload TEXT NOT NULL,ReadAt INTEGER,PRIMARY KEY(Revision,UserId));
        CREATE TABLE IF NOT EXISTS TimetableHomerooms(ClassId TEXT PRIMARY KEY,TeacherId TEXT NOT NULL);
        """);
    public int Execute(string sql,params (string Key,object? Value)[] parameters){using var db=open();using var c=Command(db,sql,parameters);return c.ExecuteNonQuery();}
    public static SqliteCommand Command(SqliteConnection db,string sql,params (string Key,object? Value)[] parameters){var c=db.CreateCommand();c.CommandText=sql;foreach(var (key,value) in parameters)c.Parameters.AddWithValue(key,value??DBNull.Value);return c;}
    public List<T> Query<T>(string sql,Func<SqliteDataReader,T> map,params (string Key,object? Value)[] parameters){using var db=open();using var c=Command(db,sql,parameters);using var r=c.ExecuteReader();var rows=new List<T>();while(r.Read())rows.Add(map(r));return rows;}
    public long Revision=>Query("SELECT COALESCE(MAX(Revision),0) FROM TimetableBatches",r=>r.GetInt64(0))[0];
    public TimetableBatch[] Batches()=>Query("SELECT Payload FROM TimetableBatches ORDER BY Revision",r=>JsonSerializer.Deserialize<TimetableBatch>(r.GetString(0),Json)!).ToArray();
    public TimetableBatch? ByClient(string owner,string client)=>Query("SELECT Payload FROM TimetableBatches WHERE Owner=$owner AND ClientId=$client",r=>JsonSerializer.Deserialize<TimetableBatch>(r.GetString(0),Json)!, ("$owner",owner),("$client",client)).FirstOrDefault();
    public TimetableBatch Insert(TimetableBatch source,TimetableNotice[] notices)
    {
        using var db=open();using var tx=db.BeginTransaction();
        using(var insert=Command(db,"INSERT INTO TimetableBatches(Id,Owner,ClientId,Payload) VALUES($id,$owner,$client,'')",("$id",source.Id),("$owner",source.Owner),("$client",source.ClientId))){insert.Transaction=tx;insert.ExecuteNonQuery();}
        long revision;using(var last=Command(db,"SELECT last_insert_rowid()")){last.Transaction=tx;revision=(long)last.ExecuteScalar()!;}
        var batch=source with{Revision=revision};using(var update=Command(db,"UPDATE TimetableBatches SET Payload=$payload WHERE Id=$id",("$payload",JsonSerializer.Serialize(batch,Json)),("$id",batch.Id))){update.Transaction=tx;update.ExecuteNonQuery();}
        foreach(var n in notices){using var add=Command(db,"INSERT INTO TimetableNotices VALUES($revision,$user,$payload,NULL)",("$revision",revision),("$user",n.UserId),("$payload",JsonSerializer.Serialize(n with{Revision=revision},Json)));add.Transaction=tx;add.ExecuteNonQuery();}
        tx.Commit();return batch;
    }
    public void Replicate(TimetableDelivery delivery)
    {
        var batch=delivery.Batch;
        using var db=open();using var tx=db.BeginTransaction();
        using(var old=Command(db,"SELECT Id,Payload FROM TimetableBatches WHERE Revision=$rev OR Id=$id",("$rev",batch.Revision),("$id",batch.Id)))
        {old.Transaction=tx;using var r=old.ExecuteReader();if(r.Read()){if(r.GetString(0)!=batch.Id||r.GetString(1)!=JsonSerializer.Serialize(batch,Json))throw new FilePolicyException("같은 시간표 버전의 내용을 바꿀 수 없습니다.",409);return;}}
        using(var last=Command(db,"SELECT COALESCE(MAX(Revision),0) FROM TimetableBatches")){last.Transaction=tx;if(batch.Revision<=(long)last.ExecuteScalar()!)throw new FilePolicyException("이전 시간표 버전을 적용할 수 없습니다.",409);}
        using(var add=Command(db,"INSERT INTO TimetableBatches(Revision,Id,Owner,ClientId,Payload,Delivered) VALUES($rev,$id,$owner,$client,$payload,1)",("$rev",batch.Revision),("$id",batch.Id),("$owner",batch.Owner),("$client",batch.ClientId),("$payload",JsonSerializer.Serialize(batch,Json)))){add.Transaction=tx;add.ExecuteNonQuery();}
        using(var clear=Command(db,"DELETE FROM TimetableHomerooms")){clear.Transaction=tx;clear.ExecuteNonQuery();}
        foreach(var room in delivery.Homerooms){using var add=Command(db,"INSERT INTO TimetableHomerooms VALUES($class,$teacher)",("$class",room.ClassId),("$teacher",room.TeacherId));add.Transaction=tx;add.ExecuteNonQuery();}
        foreach(var n in delivery.Notices){using var add=Command(db,"INSERT INTO TimetableNotices VALUES($rev,$user,$payload,NULL)",("$rev",batch.Revision),("$user",n.UserId),("$payload",JsonSerializer.Serialize(n,Json)));add.Transaction=tx;add.ExecuteNonQuery();}
        tx.Commit();
    }
    public TimetableHomeroom[] Homerooms()=>Query("SELECT ClassId,TeacherId FROM TimetableHomerooms",r=>new TimetableHomeroom(r.GetString(0),r.GetString(1))).ToArray();
    public TimetableNotice[] Notices(string user)=>Query("SELECT Payload,ReadAt FROM TimetableNotices WHERE UserId=$user ORDER BY Revision DESC LIMIT 100",r=>JsonSerializer.Deserialize<TimetableNotice>(r.GetString(0),Json)! with{ReadAt=r.IsDBNull(1)?null:r.GetInt64(1)},("$user",user)).ToArray();
    public TimetableNotice[] BatchNotices(long revision)=>Query("SELECT Payload FROM TimetableNotices WHERE Revision=$rev",r=>JsonSerializer.Deserialize<TimetableNotice>(r.GetString(0),Json)!, ("$rev",revision)).ToArray();
    public static DateOnly Date(string value)=>DateOnly.TryParseExact(value,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var date)?date:throw new FilePolicyException("날짜는 yyyy-MM-dd로 입력하세요.");
    public TimetableDay Day(string date,string[] classes)=>Resolve(Batches(),date,classes);
    public static TimetableDay Resolve(IEnumerable<TimetableBatch> source,string date,string[] classes)
    {
        var when=Date(date);var weekday=((int)when.DayOfWeek+6)%7+1;var rows=source.ToArray();var slots=new List<TimetableSlot>();var missing=new List<string>();long published=0;
        foreach(var room in classes.Distinct())
        {
            bool Contains(TimetableBatch b)=>b.Cells.Any(c=>c.ClassId==room);
            var baseline=rows.Where(b=>b.Kind=="base"&&string.CompareOrdinal(b.Start,date)<=0&&string.CompareOrdinal(b.End,date)>=0&&Contains(b)).MaxBy(b=>b.Revision);
            if(baseline?.Withdrawn==true)baseline=null;
            var daily=rows.Where(b=>b.Kind=="daily"&&b.Date==date&&Contains(b)).MaxBy(b=>b.Revision);if(daily?.Withdrawn==true)daily=null;
            if(baseline is null&&daily is null){missing.Add(room);continue;}
            var selected=daily??baseline!;published=Math.Max(published,selected.PublishedAt);
            for(var period=1;period<=12;period++)
            {
                var before=baseline?.Cells.SingleOrDefault(c=>c.ClassId==room&&c.Day==weekday&&c.Period==period);
                var cell=selected.Cells.SingleOrDefault(c=>c.ClassId==room&&c.Day==weekday&&c.Period==period);
                var sample=selected.Cells.First(c=>c.ClassId==room);
                var changed=daily is not null&&((cell?.Subject??"")!=(before?.Subject??"")||(cell?.TeacherId??"")!=(before?.TeacherId??""));
                slots.Add(new(room,sample.ClassName,period,cell?.Subject??"",cell?.TeacherId??"",cell?.TeacherName??"",changed,before?.Subject??"",before?.TeacherName??"",daily is null?"base":"daily"));
            }
        }
        return new(date,rows.Select(b=>b.Revision).DefaultIfEmpty().Max(),published,missing.ToArray(),slots.ToArray());
    }
    public static void Validate(TimetableBatch b)
    {
        if(!Guid.TryParseExact(b.Id,"N",out _)||!Guid.TryParseExact(b.ClientId,"N",out _)||!Guid.TryParseExact(b.Owner,"N",out _)||b.Kind is not("base"or"daily")||b.Year is <2000 or >2100||b.Semester is <1 or >2||b.Cells is null||b.Cells.Length is <1 or >10000||b.FileName is null||b.FileName.Length>200)throw new FilePolicyException("시간표 게시 형식을 확인하세요.");
        var start=Date(b.Start);var end=Date(b.End);if(start>end||start.Year!=b.Year||end>start.AddYears(1))throw new FilePolicyException("학년도와 적용 기간을 확인하세요.");
        if(b.Kind=="daily"&&(b.Date!=b.Start||b.Date!=b.End))throw new FilePolicyException("일자별 수업일을 확인하세요.");
        if(b.Cells.Any(c=>c is null||!Guid.TryParseExact(c.ClassId,"N",out _)||c.ClassName is null||c.Subject is null||c.TeacherName is null||c.TeacherId is null||c.ClassName.Length is <1 or >100||c.Day is <1 or >7||c.Period is <1 or >12||c.Subject.Length>200||c.TeacherName.Length>100||c.Subject.Length>0&&!Guid.TryParseExact(c.TeacherId,"N",out _)||c.Subject.Length==0&&(c.TeacherId.Length>0||c.TeacherName.Length>0))||b.Cells.GroupBy(c=>(c.ClassId,c.Day,c.Period)).Any(g=>g.Count()>1))throw new FilePolicyException("학급·교사·교시 정보가 올바르지 않습니다.");
        if(b.Kind=="daily"&&b.Cells.Any(c=>c.Day!=((int)start.DayOfWeek+6)%7+1))throw new FilePolicyException("일자별 시간표 날짜와 요일이 다릅니다.");
    }
}
