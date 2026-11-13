using Microsoft.Data.Sqlite;
using System.Text.Json;
using StargateGame.Shared;
namespace StargateGame.Persistence;

public static class NpcStorage
{
    static SqliteConnection Open(string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=file,Pooling=false}.ToString());db.Open();
        using var c=db.CreateCommand();
        c.CommandText="CREATE TABLE IF NOT EXISTS npcs(location TEXT PRIMARY KEY,schema_version INTEGER NOT NULL,revision INTEGER NOT NULL,json TEXT NOT NULL)";c.ExecuteNonQuery();
        return db;
    }
    public static Task<(long Revision,NpcRecord[] Records)> Load(string file,Guid location)=>Task.Run(()=>
    {
        using var db=Open(file);using var c=db.CreateCommand();c.CommandText="SELECT schema_version,revision,json FROM npcs WHERE location=$id";c.Parameters.AddWithValue("$id",location.ToString());
        using var r=c.ExecuteReader();if(!r.Read())return(0L,Array.Empty<NpcRecord>());
        if(r.GetInt32(0)!=NpcRules.Schema)throw new InvalidDataException("Unsupported NPC save schema.");
        var records=JsonSerializer.Deserialize<NpcRecord[]>(r.GetString(2),Protocol.Json)??[];
        if(records.Length>NpcRules.Limit||records.Any(n=>n is null||!NpcRules.Valid(n)||n.Location!=location)||records.Select(n=>n.Id).Distinct().Count()!=records.Length)
            throw new InvalidDataException("Invalid NPC save records.");
        return(r.GetInt64(1),records);
    });
    public static Task Save(string file,Guid location,long revision,NpcRecord[] records)=>Task.Run(()=>
    {
        if(revision<0||records.Length>NpcRules.Limit||records.Any(n=>n is null||!NpcRules.Valid(n)||n.Location!=location)||records.Select(n=>n.Id).Distinct().Count()!=records.Length)
            throw new InvalidDataException("Invalid NPC save records.");
        using var db=Open(file);using var c=db.CreateCommand();
        c.CommandText="INSERT INTO npcs VALUES($id,$schema,$revision,$json) ON CONFLICT(location) DO UPDATE SET schema_version=excluded.schema_version,revision=excluded.revision,json=excluded.json";
        c.Parameters.AddWithValue("$id",location.ToString());c.Parameters.AddWithValue("$schema",NpcRules.Schema);c.Parameters.AddWithValue("$revision",revision);
        c.Parameters.AddWithValue("$json",JsonSerializer.Serialize(records,Protocol.Json));c.ExecuteNonQuery();
    });
}
