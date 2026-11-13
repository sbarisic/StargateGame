using System.Numerics;
using FishGfx;
using FishGfx.Game;
using FishGfx.Graphics;
using StargateGame.Shared;
namespace StargateGame.Client;

sealed partial class GameApp
{
    bool spawnMenuOpen, spawnMenuSuppressed;
    int spawnSelection, spawnScroll;
    Vector2 spawnWorldCursor;
    enum SpawnAction { Prop, Cable, Npc, NpcOrders }
    sealed record SpawnItem(string Model, string Name, SpawnAction Action);
    static readonly SpawnItem[] spawnItems = [new("wooden-crate", "Box", SpawnAction.Prop), new("barrel-red", "Barrel", SpawnAction.Prop), new("wooden-lantern", "Wooden Lantern", SpawnAction.Prop), new("zpm-hub", "ZPM Hub", SpawnAction.Prop), new("zpm", "ZPM", SpawnAction.Prop), new("cable", "Cable tool", SpawnAction.Cable), new("military-soldier", "Military Soldier", SpawnAction.Npc), new("military-soldier", "NPC Orders", SpawnAction.NpcOrders)];
    bool CableSelected => spawnItems[spawnSelection].Action == SpawnAction.Cable;
    int SpawnColumns => UiWidth >= 800 ? 2 : 1;
    int SpawnTotalRows => (spawnItems.Length + SpawnColumns - 1) / SpawnColumns;
    float SpawnItemHeight => Math.Clamp((SpawnPanel.H - 65 - (CableSelected ? 100 : 30)) / SpawnTotalRows - 10, 75, 135);
    int SpawnRows => Math.Max(1, (int)((SpawnPanel.H - 65 - (CableSelected ? 100 : 30)) / (SpawnItemHeight + 10)));
    int SpawnFirstItem => spawnScroll * SpawnColumns;
    int SpawnEndItem => Math.Min(spawnItems.Length, SpawnFirstItem + SpawnRows * SpawnColumns);
    bool SpawnCanScroll => SpawnTotalRows > SpawnRows;
    void ClampSpawnScroll() => spawnScroll = Math.Clamp(spawnScroll, 0, Math.Max(0, SpawnTotalRows - SpawnRows));
    UiRect SpawnPanel => new(20,80,SpawnColumns == 2 ? 480 : 240,Math.Min(650,UiHeight-100));
    UiRect SpawnTile(int index)
    {
        float width=(SpawnPanel.W-28-(SpawnColumns-1)*10)/SpawnColumns;
        return new(SpawnPanel.X+14+index%SpawnColumns*(width+10),SpawnPanel.Y+65+(index/SpawnColumns-spawnScroll)*(SpawnItemHeight+10),width,SpawnItemHeight);
    }
    UiRect SpawnScrollButton(bool down) => new(SpawnPanel.X+SpawnPanel.W-(down?42:76),SpawnPanel.Y+SpawnPanel.H-26,28,22);
    void SetSpawnMenu(bool open)
    {
        if(open && session?.Reloading==true)return;
        if(open)spawnWorldCursor=new(UiWidth*.5f,UiHeight*.5f);
        if(open&&physicsGunEquipped)
        {
            if(session?.Carrying==true)session.Carry(CarryAction.Drop);
            if(scene is not null)scene.PhysicsGunBeam=null;
        }
        spawnMenuOpen=open;
        window.CaptureCursor=!open&&!paused&&!Smoke&&!UiPreview&&!TestAutomated;
        mouseDelta=Vector2.Zero; jumpQueued=false;
        uiInputConsumed=true; uiMouseGuard=true;
        CancelDemolition(); scene?.Preview(null,false); scene?.Demolition(null,[],false);
    }
    bool SpawnMenuInput(InputManager input)
    {
        if(!input.IsKeyDown(Key.Q))spawnMenuSuppressed=false;
        bool eligible=session?.World.Definition.Fixture=="gate-pair"&&!paused&&!planetPanel&&!bookOpen&&!inventoryOpen&&!buildPaletteOpen&&!testingPanel&&session?.TransitLocked==false&&session?.PlanetVisualPending==false;
        bool held=eligible&&input.IsKeyDown(Key.Q)&&!spawnMenuSuppressed;
        if(spawnMenuOpen&&!held){SetSpawnMenu(false);return true;}
        if(!spawnMenuOpen&&held)SetSpawnMenu(true);
        if(!spawnMenuOpen)return false;
        uiInputConsumed=true;
        ClampSpawnScroll();
        if(!SpawnPanel.Contains(UiMouse))spawnWorldCursor=UiMouse;
        if(input.WasKeyPressed(Key.Escape)){spawnMenuSuppressed=true;SetSpawnMenu(false);return true;}
        if(input.WasMouseButtonPressed(MouseButton.Left))
        {
            if(SpawnCanScroll)
                foreach(bool down in new[]{false,true})
                    if(SpawnScrollButton(down).Contains(UiMouse)){spawnScroll+=down?1:-1;ClampSpawnScroll();return true;}
            if(NpcMenuClick())return true;
            if(CableSelected&&CableMenuClick())return true;
            for(int i=SpawnFirstItem;i<SpawnEndItem;i++)
                if(SpawnTile(i).Contains(UiMouse)){spawnSelection=i;if(CableSelected)EquipCable();else if(spawnItems[i].Action==SpawnAction.NpcOrders)EquipNpcs();else{if(cableTool)ExitCable();ExitNpcs();}return true;}
            if(!SpawnPanel.Contains(UiMouse))SpawnAtCursor();
        }
        return true;
    }
    void SpawnAtCursor()
    {
        if(session is null||session.BuildPending||CableSelected||spawnItems[spawnSelection].Action==SpawnAction.NpcOrders)return;
        if(!Matrix4x4.Invert(camera.View*camera.Projection,out var inverse))return;
        var far=Vector4.Transform(new Vector4(spawnWorldCursor.X/UiWidth*2-1,1-spawnWorldCursor.Y/UiHeight*2,1,1),inverse);
        if(Math.Abs(far.W)<.000001f)return;
        if(spawnItems[spawnSelection].Action==SpawnAction.Npc){session.Npc(new(Guid.NewGuid(),"spawn",Origin:camera.Position,Point:new Vector3(far.X,far.Y,far.Z)/far.W));return;}
        session.Build(new(Guid.NewGuid(),session.Changes.Revision,"spawn-prop",Anchor:camera.Position,
            Prefab:spawnItems[spawnSelection].Model,HitPoint:new Vector3(far.X,far.Y,far.Z)/far.W));
    }
    void DrawSpawnMenu(RenderPass pass)
    {
        ClampSpawnScroll();
        var panel=SpawnPanel;
        Panel(pass,panel,true);
        Text(pass,panel.X+14,panel.Y+18,"SANDBOX / SPAWN",20,UiTheme.Amber);
        Text(pass,panel.X+14,panel.Y+44,"Hold Q to browse",14,UiTheme.Muted);
        for(int i=SpawnFirstItem;i<SpawnEndItem;i++)
        {
            var tile=SpawnTile(i); var key=spawnItems[i].Model;
            RegisterTestUi("spawn/" + key + "/" + spawnItems[i].Action, spawnItems[i].Name, tile, true);
            ui.Frame(pass,tile,tile.Contains(UiMouse)?UiTheme.Hover:UiTheme.Panel,selected:i==spawnSelection);
            if(!buildThumbnails.TryGetValue(key,out var tex)&&!failedBuildThumbnails.Contains(key))
            {
                try
                {
                    tex=window.Graphics.LoadTexture(Path.Combine(AppContext.BaseDirectory,"assets/gui/building",key+".png"),
                        new TextureLoadOptions {MipLevels=4,Sampling=new(TextureFilter.LinearMipmapLinear,TextureFilter.Linear)});
                    buildThumbnails.Add(key,tex);
                }
                catch(Exception ex){failedBuildThumbnails.Add(key);Console.Error.WriteLine($"Spawn thumbnail {key}: {ex.Message}");}
            }
            if(tex is not null)pass.DrawTexturedRectangle(tile.X+6,UiHeight-tile.Y-tile.H+29,tile.W-12,tile.H-35,0,0,1,1,Color.White,tex);
            Text(pass,tile.X+12,tile.Y+tile.H-25,spawnItems[i].Name,16,UiTheme.Text);
        }
        if(CableSelected)DrawCableMenu(pass); DrawNpcMenu(pass);
        Text(pass,panel.X+14,panel.Y+panel.H-18,SpawnCanScroll?$"{SpawnFirstItem+1}-{SpawnEndItem}/{spawnItems.Length} / Wheel: scroll":$"All {spawnItems.Length} items",12,UiTheme.Muted);
        if(SpawnCanScroll)
            foreach(bool down in new[]{false,true})
            {
                var r=SpawnScrollButton(down);ui.Frame(pass,r,r.Contains(UiMouse)?UiTheme.Hover:UiTheme.Panel);
                Text(pass,r.X+8,r.Y+3,down?"v":"^",14,UiTheme.Text);
            }
        float hintX=panel.X+panel.W+20;
        Text(pass,hintX,UiHeight-60,npcTool?npcHint:CableSelected ? "Release Q / Click two surfaces / Wheel: slack / Right: cancel or remove" : "Select item / Click outside menu to spawn",16,UiTheme.Amber);
        if(npcTool)Text(pass,hintX,UiHeight-36,NpcOrderFeedback,15,UiTheme.Text);
        else if(spawnItems[spawnSelection].Action is SpawnAction.Npc or SpawnAction.NpcOrders)
            Text(pass,hintX,UiHeight-36,session?.NpcPending==true?"Sending NPC command...":session?.LastNpcReply?.Reason??"",15,UiTheme.Text);
        else if(session?.BuildAction=="spawn-prop")
            Text(pass,hintX,UiHeight-36,session.BuildPending?"Spawning...":session.LastBuildResult?.Reason??"",15,UiTheme.Text);
    }
}
