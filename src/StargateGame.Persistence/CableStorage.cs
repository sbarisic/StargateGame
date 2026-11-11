using Microsoft.Data.Sqlite;
using System.Text.Json;
using StargateGame.Shared;
namespace StargateGame.Persistence;

// Separate cable transactions never participate in collision preparation.
public static class CableStorage
{
    public static async Task<(long Revision,CableRecord[] Cables)> Load(string file, Guid location)
    {
        return await Task.Run(() =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            using var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=file,Pooling=false}.ToString());db.Open();
            using var schema=db.CreateCommand();schema.CommandText="CREATE TABLE IF NOT EXISTS cables(location TEXT PRIMARY KEY,revision INTEGER NOT NULL,json TEXT NOT NULL);";schema.ExecuteNonQuery();
            using var cmd=db.CreateCommand();cmd.CommandText="SELECT revision,json FROM cables WHERE location=$id";cmd.Parameters.AddWithValue("$id",location.ToString());
            using var r=cmd.ExecuteReader();
            if(!r.Read())return (0L,Array.Empty<CableRecord>());
            var cables=JsonSerializer.Deserialize<CableRecord[]>(r.GetString(1),Protocol.Json)??[];
            if(cables.Length>CableRules.Limit || cables.Any(c=>c is null||c.Id==Guid.Empty||!CableRules.Valid(c.A)||!CableRules.Valid(c.B)||!float.IsFinite(c.Length)||c.Length<=0||c.Length>200||c.Color is <0 or >4||c.Diameter is not (.01f or .02f or .04f or .05f or .08f or .1f or .11f or .14f)))throw new InvalidDataException("Invalid saved cables.");
            return(r.GetInt64(0),cables);
        });
    }
    public static Task Save(string file,Guid location,long revision,CableRecord[] cables) => Task.Run(() =>
    {
        using var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=file,Pooling=false}.ToString());db.Open();
        using var cmd=db.CreateCommand();cmd.CommandText="INSERT INTO cables(location,revision,json) VALUES($id,$rev,$json) ON CONFLICT(location) DO UPDATE SET revision=excluded.revision,json=excluded.json";
        cmd.Parameters.AddWithValue("$id",location.ToString());cmd.Parameters.AddWithValue("$rev",revision);cmd.Parameters.AddWithValue("$json",JsonSerializer.Serialize(cables,Protocol.Json));cmd.ExecuteNonQuery();
    });
}
