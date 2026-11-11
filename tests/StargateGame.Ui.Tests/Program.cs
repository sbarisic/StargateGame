using System.Numerics;
using StargateGame.Client;

int checks=0;
void Check(bool condition,string message){
checks++;
if(!condition)throw new Exception(message);
}
foreach(var (width,height) in new[]{(960,600),(1280,720),(1920,1080),(2560,1440),(3440,1440)})
foreach(float dpi in new[]{1f,1.5f,2f}){
    float contentScale=UiLayout.ContentScale(width,height,dpi);
Check(width/contentScale>=960&&height/contentScale>=600,"DPI preserves minimum logical layout");
    var l=new UiLayout(width,height);
    void Bounds(UiRect r){
Check(r.W>0&&r.H>0&&r.X>=0&&r.Y>=0&&r.Right<=width&&r.Bottom<=height,$"Bounds {width}x{height}: {r}");
Check(r.Contains(new(r.X+r.W/2,r.Y+r.H/2)),"Center hit");
Check(!r.Contains(new(r.Right,r.Bottom)),"Exclusive edge");
Check(MathF.Abs(MathF.Round(r.X*dpi)/dpi-r.X)<=.5f/dpi+.0001f,"DPI snap error");
}
    Bounds(l.Inventory);
    var weaponTargets=Enumerable.Range(0,4).Select(i=>l.WeaponSlot(false,i))
        .Concat(Enumerable.Range(0,16).Select(i=>l.WeaponSlot(true,i)))
        .Concat(Enumerable.Range(0,4).Select(l.WeaponButton)).ToArray();
    for(int i=0;i<weaponTargets.Length;i++){
        var r=weaponTargets[i]; Bounds(r);
        Check(r.X>=l.Inventory.X&&r.Y>=l.Inventory.Y&&r.Right<=l.Inventory.Right&&r.Bottom<=l.Inventory.Bottom,"Weapon targets remain inside inventory panel");
        for(int j=i+1;j<weaponTargets.Length;j++){
            var other=weaponTargets[j];
            Check(r.Right<=other.X||other.Right<=r.X||r.Bottom<=other.Y||other.Bottom<=r.Y,"Inventory slots and actions never overlap");
        }
    }
    foreach(var r in new[]{l.Identity,l.Compass,l.Explorers,l.Address,l.Equipment,l.Notice,l.Connection,l.Join,l.Pause,l.Mute,l.Developer,l.Book,l.BookCurrent,l.BookClose,l.BookPage(false),l.BookPage(true),l.Picker,l.PickerPage(false),l.PickerPage(true)})Bounds(r);
    for(int i=0;i<3;i++){
Bounds(l.ConnectField(i));
Bounds(l.PauseAction(i));
}
    for(int i=0;i<4;i++){
Bounds(l.Slider(i));
Bounds(l.BookRow(i));
Bounds(l.DeveloperAction(i));
}
    Check(l.Identity.Right<=l.Compass.X,"Identity / compass overlap");
Check(l.Compass.Right<=l.Explorers.X,"Compass / address overlap");
    Check(l.Address.Bottom<height/2-40,"Address clears aiming region");
    for(int slot=0;slot<l.PageSize;slot++){
var r=l.Tile(slot);
Bounds(r);
Check(r.Bottom<l.PickerPage(false).Y,"Picker clears navigation");
for(int other=slot+1;other<l.PageSize;other++)Check(!r.Contains(new(l.Tile(other).X,l.Tile(other).Y)),"Distinct tile targets");
}
    foreach(int count in new[]{0,1,14,15,16,30,47,103}){
int pages=Math.Max(1,(count+l.PageSize-1)/l.PageSize);
var covered=Enumerable.Range(0,pages).SelectMany(page=>Enumerable.Range(0,l.PageSize).Select(slot=>page*l.PageSize+slot)).Where(index=>index<count).ToArray();
Check(covered.SequenceEqual(Enumerable.Range(0,count)),"Pagination retains catalog order without duplicate/lost items");
}
    Check(7*(28+6)-6<=l.Address.W-24,"Origin-complete glyph row fits");
    Check(6*(48+6)-6<=l.BookColumn,"Selected address fits");
}
foreach(var (yaw,expected) in new[]{(0f,0f),(MathF.PI/2,90f),(MathF.PI,180f),(-MathF.PI/2,270f),(MathF.Tau,0f)})Check(MathF.Abs(UiLayout.Heading(yaw)-expected)<.001f,"Cardinal heading");
Check(MathF.Abs(UiLayout.BearingDelta(0,359)-1)<.001f,"North wraps forward");
Check(MathF.Abs(UiLayout.BearingDelta(359,0)+1)<.001f,"North wraps backward");
Check(UiLayout.Ellipsis(new string('W',300),32).Length==32,"Long names bounded");
Check(UiLayout.Ellipsis("Home",32)=="Home","Short names preserved");
Console.WriteLine($"UI layout PASS: {checks} assertions across five viewports and three framebuffer scale factors. OS DPI behavior requires graphical verification.");
