using System.Numerics;
using FishGfx.Game;
using FishGfx.Graphics;
using StargateGame.Shared;
using StargateGame.Building;
using StargateGame.Physics;
namespace StargateGame.Client;
sealed partial class GameApp
{
    bool npcTool;
    Guid selectedNpc,npcToolLocation,npcSelectionCandidate;
    NpcOrderKind? enteringNpcOrder;
    readonly List<Vector3> npcOrderPoints=[];
    string npcHint="Left: select soldier / Hold Q: orders / Esc: exit";
    void EquipNpcs()
    {
        if(cableTool)ExitCable();if(session?.Carrying==true)session.Carry(CarryAction.Drop);
        physicsGunEquipped=false;buildMode=false;npcTool=true;CancelDemolition();
        SyncNpcSelection();
        if(selectedNpc==Guid.Empty)npcSelectionCandidate=session?.LastSpawnedNpc??Guid.Empty;
        SyncNpcSelection();session?.ClearNpcFeedback();
        npcHint=selectedNpc==Guid.Empty?"Release Q / Left click a soldier to select it":"Soldier selected / Choose an order";
        if(scene is not null){scene.PhysicsGunEquipped=false;scene.PhysicsGunBeam=null;}
    }
    void ExitNpcs(){npcTool=false;enteringNpcOrder=null;npcOrderPoints.Clear();selectedNpc=npcSelectionCandidate=Guid.Empty;if(scene is not null)scene.NpcToolLines=[];}
    void SyncNpcSelection()
    {
        if(session is null)return;
        if(npcToolLocation!=session.World.Definition.LocationId)
        {selectedNpc=npcSelectionCandidate=Guid.Empty;enteringNpcOrder=null;npcOrderPoints.Clear();npcToolLocation=session.World.Definition.LocationId;}
        if(npcSelectionCandidate!=Guid.Empty&&session.Npcs.Records.Any(n=>n.Id==npcSelectionCandidate))
        {selectedNpc=npcSelectionCandidate;npcSelectionCandidate=Guid.Empty;npcHint="Soldier selected / Choose an order";}
        if(selectedNpc!=Guid.Empty&&!session.Npcs.Records.Any(n=>n.Id==selectedNpc))
        {selectedNpc=Guid.Empty;enteringNpcOrder=null;npcOrderPoints.Clear();npcHint="Soldier unavailable / Select another soldier";}
    }
    string NpcOrderFeedback
    {
        get
        {
            if(session?.NpcPending==true)return "Sending NPC command...";
            var status=session?.Npcs.Status.FirstOrDefault(s=>s.Id==selectedNpc);
            string live=status is null?"No soldier selected":$"{status.Schedule}: {status.Task} / {status.Navigation} {status.Reason}";
            return session?.LastNpcReply is {} reply?reply.Reason+(reply.Accepted&&status is not null?" / "+live:""):live;
        }
    }
    bool NpcInput(InputManager input)
    {
        if(!npcTool||session is null)return false;
        carryingConsumed=true;
        SyncNpcSelection();
        if(paused||spawnMenuOpen||bookOpen||inventoryOpen||planetPanel||session.TransitLocked)return true;
        if(input.WasMouseButtonPressed(MouseButton.Right)){enteringNpcOrder=null;npcOrderPoints.Clear();session.ClearNpcFeedback();npcHint="Point entry cancelled / Hold Q to choose an order";return true;}
        if(enteringNpcOrder==NpcOrderKind.Patrol&&input.WasKeyPressed(Key.Enter)&&npcOrderPoints.Count>=2)SendNpcOrder(NpcOrderKind.Patrol);
        if(scene is not null)
        {
            var lines=new List<DebugLine>();
            foreach(var n in session.PresentedNpcs().Where(n=>n.Id==selectedNpc))
            {
                for(int i=0;i<24;i++){float a=i*MathF.Tau/24,b=(i+1)*MathF.Tau/24;lines.Add(new(n.Position+new Vector3(MathF.Cos(a)*.5f,.08f,MathF.Sin(a)*.5f),n.Position+new Vector3(MathF.Cos(b)*.5f,.08f,MathF.Sin(b)*.5f),new(0,1,1)));}
            }
            for(int i=1;i<npcOrderPoints.Count;i++)lines.Add(new(npcOrderPoints[i-1]+Vector3.UnitY*.1f,npcOrderPoints[i]+Vector3.UnitY*.1f,new(1,.7f,0)));
            foreach(var point in npcOrderPoints){lines.Add(new(point,point+Vector3.UnitY*.7f,new(1,.7f,0)));lines.Add(new(point-Vector3.UnitX*.2f,point+Vector3.UnitX*.2f,new(1,.7f,0)));}
            scene.NpcToolLines=lines;
        }
        if(!input.WasMouseButtonPressed(MouseButton.Left)||session.NpcPending)return true;
        if(enteringNpcOrder is {} kind)
        {
            var hit=session.Physics.CablePick(camera.Position,camera.WorldForwardNormal,session.Local.Id,100);
            if(hit is null||!session.Physics.CablePoint(hit,out var point,out var normal)||normal.Y<.707f){npcHint="Aim at a walkable surface.";return true;}
            var npc=session.Npcs.Records.FirstOrDefault(n=>n.Id==selectedNpc);
            if(npc is null||Vector3.Distance(npc.Position,point)>100){npcHint="Destination must be within 100 m of the soldier.";return true;}
            if(npcOrderPoints.Count<16)npcOrderPoints.Add(point);
            if(kind!=NpcOrderKind.Patrol)SendNpcOrder(kind);
            else npcHint=$"Patrol: {npcOrderPoints.Count}/16 points / Enter: confirm / Right: cancel";
            return true;
        }
        float nearest=20;Guid target=Guid.Empty;
        foreach(var n in session.PresentedNpcs())
        {
            var bounds=new BuildBounds(n.Position-new Vector3(.32f,0,.32f),n.Position+new Vector3(.32f,1.8f,.32f));
            if(BuildingQuery.RayBounds(camera.Position,camera.WorldForwardNormal,bounds,nearest,out float distance)&&
                !session.Physics.InteractionBlocked(camera.Position,camera.WorldForwardNormal,Math.Max(0,distance-.05f),ignorePlayer:session.Local.Id,ignoreNpc:n.Id))
            {nearest=distance;target=n.Id;}
        }
        selectedNpc=target;npcSelectionCandidate=Guid.Empty;session.ClearNpcFeedback();npcHint=target==Guid.Empty?"No visible soldier within 20 m.":"Soldier selected / Hold Q to choose an order";
        return true;
    }
    void SendNpcOrder(NpcOrderKind kind)
    {
        if(session is null||selectedNpc==Guid.Empty||session.NpcPending)return;
        var npc=session.Npcs.Records.FirstOrDefault(n=>n.Id==selectedNpc);if(npc is null)return;
        var points=kind==NpcOrderKind.Follow?[]:kind==NpcOrderKind.Hold?new[]{npc.Position}:npcOrderPoints.ToArray();
        session.Npc(new(Guid.NewGuid(),"order",selectedNpc,new(kind,points,kind==NpcOrderKind.Follow?session.Local.Id:0)));
        npcHint=$"{kind} requested / Hold Q to change orders";
        enteringNpcOrder=null;npcOrderPoints.Clear();
    }
    UiRect NpcOrderButton(int index)=>new(SpawnPanel.X+SpawnPanel.W+12,SpawnPanel.Y+65+index*44,220,38);
    bool NpcMenuClick()
    {
        if(!npcTool||session is null)return false;
        SyncNpcSelection();
        for(int i=0;i<7;i++)if(NpcOrderButton(i).Contains(UiMouse))
        {
            if(session.NpcPending){npcHint="Waiting for the current NPC request";return true;}
            session.ClearNpcFeedback();
            if(i==5){enteringNpcOrder=null;npcOrderPoints.Clear();npcHint="Point entry cancelled / Choose an order";return true;}
            if(selectedNpc==Guid.Empty){npcHint="Release Q / Left click a soldier first / Hold Q again";return true;}
            if(i==6){session.Npc(new(Guid.NewGuid(),"remove",selectedNpc));npcHint="Removing selected soldier...";return true;}
            var kind=i switch{0=>NpcOrderKind.Hold,1=>NpcOrderKind.MoveTo,2=>NpcOrderKind.Follow,3=>NpcOrderKind.Wander,_=>NpcOrderKind.Patrol};
            npcOrderPoints.Clear();
            if(kind is NpcOrderKind.Hold or NpcOrderKind.Follow)SendNpcOrder(kind);
            else {enteringNpcOrder=kind;npcHint=kind==NpcOrderKind.Patrol?"Release Q / Click 2–16 patrol points / Enter: confirm":$"Release Q / Left click a { (kind==NpcOrderKind.Wander?"wander home":"destination") }";}
            return true;
        }
        return false;
    }
    void DrawNpcMenu(RenderPass pass)
    {
        if(!npcTool)return;
        SyncNpcSelection();
        Text(pass,SpawnPanel.X+SpawnPanel.W+12,SpawnPanel.Y+38,selectedNpc==Guid.Empty?"NPC / No soldier selected":"NPC / Soldier selected",16,selectedNpc==Guid.Empty?UiTheme.Muted:UiTheme.Amber);
        string[] names=["Hold","Move To","Follow Me","Wander","Patrol","Cancel point entry","Remove"];
        for(int i=0;i<names.Length;i++)
        {
            bool enabled=!(session?.NpcPending??false)&&(i==5||selectedNpc!=Guid.Empty);
            Button(pass,NpcOrderButton(i),names[i],selected:enteringNpcOrder is {} order&&i==(order==NpcOrderKind.MoveTo?1:order==NpcOrderKind.Wander?3:4),enabled:enabled,size:16);
        }
    }
    void DrawNpcHud(RenderPass pass)
    {
        if(!npcTool||paused)return;
        float y=UiHeight-125;Panel(pass,new(20,y,Math.Min(780,UiWidth-40),100));
        Text(pass,32,y+10,"NPC ORDERS / Military Soldier",16,UiTheme.Amber);
        Text(pass,32,y+35,npcHint,14);
        var status=session?.Npcs.Status.FirstOrDefault(s=>s.Id==selectedNpc);
        Text(pass,32,y+57,status is null?"":$"{status.Schedule}: {status.Task} / {status.Navigation} {status.Reason}",14);
        Text(pass,32,y+78,NpcOrderFeedback,14);
    }
}
