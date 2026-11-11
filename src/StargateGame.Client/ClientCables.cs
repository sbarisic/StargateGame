using StargateGame.Shared;
namespace StargateGame.Client;
sealed partial class ClientSession
{
    public CableRecord[] Cables {get;private set;}=[];
    public string CableStatus {get;private set;}="";
    public bool CablePending => cableRequest != Guid.Empty;
    public CableReply? LastCableReply {get;private set;}
    Guid cableLocation,cableRequest;
    long cableRevision=-1,receivingCableRevision=-1;
    int cablePart,cableParts;
    readonly List<CableRecord> receivingCables=[];
    double nextCablePoll;
    void PollCables()
    {
        if(cableLocation!=World.Definition.LocationId)
        {
            cableLocation=World.Definition.LocationId;Cables=[];cableRevision=-1;nextCablePoll=0;
            cableRequest=Guid.Empty;receivingCables.Clear();receivingCableRevision=-1;LastCableReply=null;
        }
        if(Now<nextCablePoll)return;
        nextCablePoll=Now+1;
        connection.Peer.Send(new(){Kind="cable-state",LocationId=cableLocation,Cables=new(cableRevision,0,1,[])});
    }
    public void Cable(CableCommand command)
    {
        if(CablePending||TransitLocked)return;
        cableRequest=command.Request;CableStatus="Saving cable...";LastCableReply=null;
        connection.Peer.Send(new(){Kind="cable-command",CableCommand=command,LocationId=World.Definition.LocationId});
    }
    bool HandleCables(Message m)
    {
        if(m.Kind is not ("cables" or "cable-result"))return false;
        if(m.LocationId!=World.Definition.LocationId)return true;
        if(m.Kind=="cable-result")
        {
            if(m.CableReply is {} reply && reply.Request==cableRequest){LastCableReply=reply;CableStatus=reply.Reason;cableRequest=Guid.Empty;}
            return true;
        }
        var part=m.Cables??throw new InvalidDataException("Missing cables.");
        if(part.Revision<0 || part.Count is <1 or >8 || part.Part<0||part.Part>=part.Count||part.Cables is null||part.Cables.Length>32)throw new InvalidDataException("Invalid cable fragment.");
        if(part.Revision<cableRevision)return true;
        if(part.Part==0){receivingCables.Clear();receivingCableRevision=part.Revision;cablePart=0;cableParts=part.Count;}
        if(part.Revision!=receivingCableRevision||part.Part!=cablePart++||part.Count!=cableParts)throw new InvalidDataException("Cable fragment order.");
        foreach(var c in part.Cables)
            if(c is null||c.Id==Guid.Empty||!CableRules.Valid(c.A)||!CableRules.Valid(c.B)||!float.IsFinite(c.Length)||c.Length<=0||c.Length>200||c.Color is <0 or >4||c.Diameter is not (.01f or .02f or .04f or .05f or .08f or .1f or .11f or .14f))throw new InvalidDataException("Invalid cable.");
        receivingCables.AddRange(part.Cables);
        if(cablePart==cableParts)
        {
            if(receivingCables.Select(c=>c.Id).Distinct().Count()!=receivingCables.Count)throw new InvalidDataException("Duplicate cable identity.");
            Cables=receivingCables.ToArray();cableRevision=part.Revision;
        }
        return true;
    }
}
