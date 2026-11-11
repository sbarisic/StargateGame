using System.Numerics;
using FishGfx;
using FishGfx.Game;
using FishGfx.Graphics;
using StargateGame.Shared;
namespace StargateGame.Client;

sealed partial class GameApp
{
    bool spawnMenuOpen, spawnMenuSuppressed;
    int spawnSelection;
    Vector2 spawnWorldCursor;
    static readonly string[] spawnModels = ["wooden-crate", "barrel-red", "cable"];
    static readonly string[] spawnNames = ["Box", "Barrel", "Cable tool"];
    UiRect SpawnPanel => new(20,80,240,Math.Min(650,UiHeight-100));
    UiRect SpawnTile(int index) => new(SpawnPanel.X+14,SpawnPanel.Y+65+index*145,212,135);
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
        if(!SpawnPanel.Contains(UiMouse))spawnWorldCursor=UiMouse;
        if(input.WasKeyPressed(Key.Escape)){spawnMenuSuppressed=true;SetSpawnMenu(false);return true;}
        if(input.WasMouseButtonPressed(MouseButton.Left))
        {
            if(CableMenuClick())return true;
            for(int i=0;i<spawnModels.Length;i++)
                if(SpawnTile(i).Contains(UiMouse)){spawnSelection=i;if(i==2)EquipCable();else{if(cableTool)ExitCable();SpawnAtCursor();}return true;}
            if(!SpawnPanel.Contains(UiMouse))SpawnAtCursor();
        }
        return true;
    }
    void SpawnAtCursor()
    {
        if(session is null||session.BuildPending||spawnSelection==2)return;
        if(!Matrix4x4.Invert(camera.View*camera.Projection,out var inverse))return;
        var far=Vector4.Transform(new Vector4(spawnWorldCursor.X/UiWidth*2-1,1-spawnWorldCursor.Y/UiHeight*2,1,1),inverse);
        if(Math.Abs(far.W)<.000001f)return;
        session.Build(new(Guid.NewGuid(),session.Changes.Revision,"spawn-prop",Anchor:camera.Position,
            Prefab:spawnModels[spawnSelection],HitPoint:new Vector3(far.X,far.Y,far.Z)/far.W));
    }
    void DrawSpawnMenu(RenderPass pass)
    {
        var panel=SpawnPanel;
        Panel(pass,panel,true);
        Text(pass,panel.X+14,panel.Y+18,"SANDBOX / SPAWN",20,UiTheme.Amber);
        Text(pass,panel.X+14,panel.Y+44,"Hold Q to browse",14,UiTheme.Muted);
        for(int i=0;i<spawnModels.Length;i++)
        {
            var tile=SpawnTile(i); var key=spawnModels[i];
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
            if(tex is not null)pass.DrawTexturedRectangle(tile.X+6,UiHeight-tile.Y-106,tile.W-12,100,0,0,1,1,Color.White,tex);
            Text(pass,tile.X+12,tile.Y+110,spawnNames[i],16,UiTheme.Text);
        }
        DrawCableMenu(pass);
        Text(pass,280,UiHeight-60,spawnSelection==2 ? "Release Q / Click two surfaces / Wheel: slack / Right: cancel or remove" : "Click a thumbnail to spawn at the last world cursor / G: physics gun",16,UiTheme.Amber);
        if(session?.BuildAction=="spawn-prop")
            Text(pass,280,UiHeight-36,session.BuildPending?"Spawning...":session.LastBuildResult?.Reason??"",15,UiTheme.Text);
    }
}
