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
    enum SpawnAction { Prop, Cable }
    sealed record SpawnItem(string Model, string Name, SpawnAction Action);
    static readonly SpawnItem[] spawnItems = [new("wooden-crate", "Box", SpawnAction.Prop), new("barrel-red", "Barrel", SpawnAction.Prop), new("wooden-lantern", "Wooden Lantern", SpawnAction.Prop), new("cable", "Cable tool", SpawnAction.Cable)];
    bool CableSelected => spawnItems[spawnSelection].Action == SpawnAction.Cable;
    float SpawnItemHeight => Math.Clamp(SpawnPanel.H - 65 - (CableSelected ? 100 : 20) - 10, 45, 135);
    int SpawnRows => Math.Max(1, (int)((SpawnPanel.H - 65 - (CableSelected ? 100 : 20)) / (SpawnItemHeight + 10)));
    void ClampSpawnScroll() => spawnScroll = Math.Clamp(spawnScroll, 0, Math.Max(0, spawnItems.Length - SpawnRows));
    UiRect SpawnPanel => new(20,80,240,Math.Min(650,UiHeight-100));
    UiRect SpawnTile(int index) => new(SpawnPanel.X+14,SpawnPanel.Y+65+(index-spawnScroll)*(SpawnItemHeight+10),212,SpawnItemHeight);
    void SetSpawnMenu(bool open)
    {
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
            if(CableSelected&&CableMenuClick())return true;
            for(int i=spawnScroll;i<Math.Min(spawnItems.Length,spawnScroll+SpawnRows);i++)
                if(SpawnTile(i).Contains(UiMouse)){spawnSelection=i;if(CableSelected)EquipCable();else{if(cableTool)ExitCable();SpawnAtCursor();}return true;}
            if(!SpawnPanel.Contains(UiMouse))SpawnAtCursor();
        }
        return true;
    }
    void SpawnAtCursor()
    {
        if(session is null||session.BuildPending||CableSelected)return;
        if(!Matrix4x4.Invert(camera.View*camera.Projection,out var inverse))return;
        var far=Vector4.Transform(new Vector4(spawnWorldCursor.X/UiWidth*2-1,1-spawnWorldCursor.Y/UiHeight*2,1,1),inverse);
        if(Math.Abs(far.W)<.000001f)return;
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
        for(int i=spawnScroll;i<Math.Min(spawnItems.Length,spawnScroll+SpawnRows);i++)
        {
            var tile=SpawnTile(i); var key=spawnItems[i].Model;
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
        if(CableSelected)DrawCableMenu(pass);
        Text(pass,panel.X+14,panel.Y+panel.H-16,"Wheel: scroll items",12,UiTheme.Muted);
        Text(pass,280,UiHeight-60,CableSelected ? "Release Q / Click two surfaces / Wheel: slack / Right: cancel or remove" : "Click a thumbnail to spawn at the last world cursor / G: physics gun",16,UiTheme.Amber);
        if(session?.BuildAction=="spawn-prop")
            Text(pass,280,UiHeight-36,session.BuildPending?"Spawning...":session.LastBuildResult?.Reason??"",15,UiTheme.Text);
    }
}
