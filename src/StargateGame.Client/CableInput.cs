using System.Numerics;
using FishGfx.Game;
using FishGfx.Graphics;
using StargateGame.Shared;
namespace StargateGame.Client;
sealed partial class GameApp
{
    bool cableTool;
    CableAnchor? cableStart,cableHover;
    CableRecord? cablePreview;
    Guid cableDraftLocation,cableLastReply;
    float cableSlack=.15f,cableDiameter=.05f;
    int cableColor;
    string cableHint="";
    void EquipCable()
    {
        if(session?.Carrying==true)session.Carry(CarryAction.Drop);
        ExitNpcs();physicsGunEquipped=false;buildMode=false;cableTool=true;
        if(scene is not null){scene.PhysicsGunEquipped=false;scene.PhysicsGunBeam=null;}
        CancelDemolition();
    }
    void ExitCable()
    {
        cableTool=false;cableStart=null;cableHover=null;cablePreview=null;
        if(scene is not null)scene.HighlightCable=Guid.Empty;
        if(session is not null&&!session.CablePending)session.Cable(new(Guid.NewGuid(),"cancel"));
    }
    bool CableInput(InputManager input)
    {
        if(!cableTool||session is null)return false;
        carryingConsumed=true;cablePreview=null;cableHover=null;
        if(cableDraftLocation!=session.World.Definition.LocationId){cableStart=null;cableDraftLocation=session.World.Definition.LocationId;}
        if(session.LastCableReply is {} reply&&reply.Request!=cableLastReply)
        {cableLastReply=reply.Request;if(!reply.Success)cableStart=null;}
        if(paused||bookOpen||inventoryOpen||planetPanel||spawnMenuOpen||session.TransitLocked)return true;
        if(input.WasMouseButtonPressed(MouseButton.Middle))
        {
            int size=Array.IndexOf(CableRules.Diameters,cableDiameter);
            cableDiameter=CableRules.Diameters[(size+1)%CableRules.Diameters.Length];
        }
        var hit=session.Physics.CablePick(camera.Position,camera.WorldForwardNormal,session.Local.Id);
        cableHover=hit;
        if(scene is not null)scene.HighlightCable=Guid.Empty;
        var selected=scene?.PickCable(camera.Position,camera.WorldForwardNormal);
        if(selected is {} selection && !session.Physics.InteractionBlocked(camera.Position,Vector3.Normalize(selection.Point-camera.Position),Math.Max(0,Vector3.Distance(selection.Point,camera.Position)-.1f),ignorePlayer:session.Local.Id))
        {if(scene is not null)scene.HighlightCable=selection.Id;}
        else selected=null;
        cableHint=cableStart is null?"Left: first surface / Right: remove aimed cable":"Left: second surface / Right: cancel / Wheel: slack";
        if(hit is not null&&cableStart is not null&&session.Physics.CablePoint(cableStart,out var a,out _)&&session.Physics.CablePoint(hit,out var b,out _))
        {
            float span=Vector3.Distance(a,b);
            if(span>=.05f&&span<=100)cablePreview=new(Guid.Empty,cableStart,hit,span*(1+cableSlack),cableColor,cableDiameter);
        }
        if(session.CablePending)return true;
        if(input.WasMouseButtonPressed(MouseButton.Right))
        {
            if(cableStart is not null){cableStart=null;cablePreview=null;session.Cable(new(Guid.NewGuid(),"cancel"));}
            else if(selected is {} target)session.Cable(new(Guid.NewGuid(),"remove",new("world",Guid.Empty,"",target.Point,Vector3.UnitY),target.Id));
            return true;
        }
        if(input.WasMouseButtonPressed(MouseButton.Left)&&hit is not null)
        {
            if(cableStart is null){cableStart=hit;session.Cable(new(Guid.NewGuid(),"begin",hit));}
            else if(cablePreview is not null){session.Cable(new(Guid.NewGuid(),"place",hit,Slack:cableSlack,Color:cableColor,Diameter:cableDiameter));cableStart=null;cablePreview=null;}
        }
        return true;
    }
    UiRect CableColorRect(int i)=>new(SpawnPanel.X+12+i*43,SpawnPanel.Y+SpawnPanel.H-96,39,30);
    UiRect CableSizeRect(int i)=>new(SpawnPanel.X+12+i*43,SpawnPanel.Y+SpawnPanel.H-55,39,30);
    bool CableMenuClick()
    {
        for(int i=0;i<5;i++)if(CableColorRect(i).Contains(UiMouse)){cableColor=i;return true;}
        for(int i=0;i<CableRules.Diameters.Length;i++)if(CableSizeRect(i).Contains(UiMouse)){cableDiameter=CableRules.Diameters[i];return true;}
        return false;
    }
    void DrawCableMenu(RenderPass pass)
    {
        string[] names=["Blk","Blu","Red","Yel","Grn"];
        for(int i=0;i<5;i++){var r=CableColorRect(i);ui.Frame(pass,r,UiTheme.Panel,selected:i==cableColor);Text(pass,r.X+4,r.Y+8,names[i],12,UiTheme.Amber);}
        for(int i=0;i<CableRules.Diameters.Length;i++){var r=CableSizeRect(i);float d=CableRules.Diameters[i];ui.Frame(pass,r,UiTheme.Panel,selected:d==cableDiameter);Text(pass,r.X+3,r.Y+8,$"{d*100:0}cm",11);}
    }
    void DrawCableHud(RenderPass pass)
    {
        if(!cableTool||spawnMenuOpen||paused)return;
        float x=20,y=UiHeight-125;
        Panel(pass,new(x,y,Math.Min(740,UiWidth-40),90));
        Text(pass,x+12,y+10,$"CABLE / Slack {cableSlack:P0} / {cableDiameter*100:0} cm / Middle: thickness / Esc: exit",16,UiTheme.Amber);
        Text(pass,x+12,y+35,cableHint,14);
        Text(pass,x+12,y+60,session?.CableStatus??"",14);
    }
}
