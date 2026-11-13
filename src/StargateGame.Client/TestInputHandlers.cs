namespace StargateGame.Client;

sealed partial class GameApp
{
    void HandleScroll(float offset)
    {
if (planetPanel && generatorTab) { generatorScroll = Math.Clamp(generatorScroll + (offset > 0 ? -1 : 1), 0, 12 - GeneratorRows); return; }
            if (bookOpen)
            {
                bookScroll = Math.Max(0, bookScroll + (offset > 0 ? -1 : 1));
                return;
            }
            if(cableTool&&!spawnMenuOpen&&!paused){cableSlack=Math.Clamp(cableSlack+Math.Sign(offset)*.05f,0,1);return;}
            if (physicsGunEquipped && !spawnMenuOpen && !paused && session?.Carrying==true)
            {
                session.ScrollCarriedProp(offset);
                return;
            }
            if (spawnMenuOpen) { spawnScroll += offset > 0 ? -1 : 1; ClampSpawnScroll(); return; }
            if (buildPaletteOpen)
            {
                palettePage = Math.Clamp(palettePage + (offset > 0 ? -1 : 1), 0, (BuildPaletteItemCount - 1) / Layout.PageSize);
                return;
            }
            if (debugPhysics && !buildMode && !paused)
            {
                debugScroll = Math.Max(0, debugScroll + (offset > 0 ? -3 : 3));
                nextDebugText = 0;
                return;
            }
            if (buildMode && !paused)
            {
                buildSelection = (buildSelection + (offset > 0 ? 1 : BuildItemCount - 1)) % BuildItemCount;
                buildRotation = 0;
                nextBuildPreview = 0;
            }
    }
    void HandleText(string text)
    {
if (planetPanel && session is not null)
            {
                if (generatorTab) { GeneratorText(text); return; }
                if (text.All(c => char.IsDigit(c) || c == '-'))
                {
                    if (selectAll)
                    {
                        planetSeed = "";
                        selectAll = false;
                    }
                    if (planetSeed.Length + text.Length <= 30)
                        planetSeed += text;
                }
                return;
            }
            if (session is not null || Joining || text.Any(char.IsControl))
                return;
            if (selectAll)
            {
                fields[field] = "";
                selectAll = false;
            }
            if (fields[field].Length + text.Length <= (field == 2 ? 24 : field == 1 ? 5 : 128))
                fields[field] += text;
    }
}
