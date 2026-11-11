namespace StargateGame.WorldGen;

/// <summary>Metre-aware USPED and channel shaping from the approved 2D study.</summary>
// Adapted from SLTerrainGeneration (Michael Tuck, MIT). Continuous aspect and
// multi-directional runoff reduce its D8 bias. This is a bounded landscape model,
// not a mass-conserving geological simulation; iterations are not physical years.
public static class RegionalErosion
{
    public const int Iterations = 160;
    static readonly (int X, int Z)[] Neighbors = [(-1,-1),(0,-1),(1,-1),(-1,0),(1,0),(-1,1),(0,1),(1,1)];

    public static void StreamOrders(int[] downstream, int[] order, int[] stream)
    {
        Array.Clear(stream);
        var duplicate = new byte[stream.Length];
        for (int k = order.Length-1; k >= 0; k--)
        {
            int i = order[k];
            stream[i] = Math.Max(1, stream[i] + (duplicate[i] >= 2 ? 1 : 0));
            int d = downstream[i]; if (d < 0) continue;
            if (stream[i] > stream[d]) { stream[d] = stream[i]; duplicate[d] = 1; }
            else if (stream[i] == stream[d]) duplicate[d]++;
        }
    }

    /// <summary>Split hillslope runoff among all descending neighbours.</summary>
    public static void DistributedRunoff(float[] filled, int width, float cell, int[] order,
        float[] runoff, CancellationToken token = default)
    {
        Array.Fill(runoff, 1);
        int rows = filled.Length/width;
        // Flood flats can differ by a single float ULP near sea level. Keep
        // weights in double so slope^1.1 cannot underflow and lose runoff.
        Span<double> weights = stackalloc double[8];
        for (int k = order.Length-1; k >= 0; k--)
        {
            if ((k & 4095) == 0) token.ThrowIfCancellationRequested();
            int i = order[k], x = i%width, z = i/width;
            if (x == 0 || z == 0 || x == width-1 || z == rows-1) continue;
            double total = 0;
            for (int n = 0; n < 8; n++)
            {
                var (dx,dz) = Neighbors[n];
                double slope = Math.Max(0,((double)filled[i]-filled[i+dz*width+dx])/(cell*(dx!=0&&dz!=0?Math.Sqrt(2):1)));
                weights[n] = Math.Pow(slope,1.1); total += weights[n];
            }
            if (total <= 0) continue;
            for (int n = 0; n < 8; n++)
            { var (dx,dz)=Neighbors[n]; runoff[i+dz*width+dx] += (float)(runoff[i]*weights[n]/total); }
        }
    }

    internal static float Derivative(float[] field, int i, int width, float cell, bool vertical)
    {
        int p = vertical ? i/width : i%width, size = vertical ? field.Length/width : width, stride = vertical ? width : 1;
        int a = p == 0 ? i : i-stride, b = p == size-1 ? i : i+stride;
        return (field[b]-field[a])/(cell*(p == 0 || p == size-1 ? 1 : 2));
    }

    static int Reflect(int p, int n) => p < 0 ? -p-1 : p >= n ? 2*n-p-1 : p;
    /// <summary>Continue descending terrain through the open simulation boundary.</summary>
    public static void OpenBoundary(float[] height, int width)
    {
        int rows = height.Length / width;
        if (width < 3 || rows < 3) return;
        // Erosion only updates interior cells. A frozen edge otherwise becomes
        // a dam as the adjacent valley erodes. Never raise an existing outlet.
        for (int z = 1; z < rows - 1; z++)
        {
            int i = z * width;
            height[i] = Math.Min(height[i], 2 * height[i + 1] - height[i + 2]);
            i += width - 1;
            height[i] = Math.Min(height[i], 2 * height[i - 1] - height[i - 2]);
        }
        for (int x = 0; x < width; x++)
        {
            height[x] = Math.Min(height[x], 2 * height[width + x] - height[2 * width + x]);
            int i = (rows - 1) * width + x;
            height[i] = Math.Min(height[i], 2 * height[i - width] - height[i - 2 * width]);
        }
    }
    internal static void Blur(float[] input, float[] output, float[] scratch, int width, float sigma, CancellationToken token)
    {
        int rows = input.Length/width, radius = (int)(4*sigma+.5f);
        Span<float> weights = stackalloc float[radius*2+1]; float total = 0;
        for (int k = -radius; k <= radius; k++) { weights[k+radius] = MathF.Exp(-.5f*k*k/(sigma*sigma)); total += weights[k+radius]; }
        for (int k = 0; k < weights.Length; k++) weights[k] /= total;
        for (int z = 0; z < rows; z++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x++)
            {
                float sum = 0;
                for (int k = -radius; k <= radius; k++) sum += input[z*width+Reflect(x+k,width)]*weights[k+radius];
                scratch[z*width+x] = sum;
            }
        }
        for (int z = 0; z < rows; z++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x++)
            {
                float sum = 0;
                for (int k = -radius; k <= radius; k++) sum += scratch[Reflect(z+k,rows)*width+x]*weights[k+radius];
                output[z*width+x] = sum;
            }
        }
    }

    public static void Apply(float[] height, float[] sediment, int width, float cell, int seed,
        string preset, bool coastal, float seaLevel, int[] downstream, int[] accumulation,
        CancellationToken token = default, Action<string>? progress = null)
    {
        int count = height.Length, rows = count/width;
        var filled=new float[count]; var runoff=new float[count]; var stream=new int[count];
        var gx=new float[count]; var gz=new float[count]; var slope=new float[count];
        var qx=new float[count]; var qz=new float[count]; var raw=new float[count];
        var change=new float[count]; var scratch=new float[count]; var blur=new float[count];
        var moist=new float[count]; var support=new float[count]; var soil=new float[count]; var jitter=new float[count];
        var soilNoise=new RegionalTerrainNoise(unchecked(seed+55));
        for (int i=0;i<count;i++)
        {
            if(i%width==0)token.ThrowIfCancellationRequested();
            soil[i]=.035f+.025f*soilNoise.Fbm(i%width*cell,i/width*cell,700,4);
            jitter[i]=.75f+(PlanetPlan.Hash(seed,i,82,11234)&65535)/65535f*.25f;
        }
        for(int iteration=0;iteration<Iterations;iteration++)
        {
            token.ThrowIfCancellationRequested();
            if(iteration%8==0)progress?.Invoke($"Erosion {iteration}/{Iterations}");
            var order=RegionalHydrology.Route(height,width,cell,downstream,accumulation,token,filled);
            StreamOrders(downstream,order,stream);
            DistributedRunoff(filled,width,cell,order,runoff,token);
            for(int i=0;i<count;i++)raw[i]=MathF.Log(1+runoff[i]);
            Blur(raw,moist,scratch,width,3,token);
            Blur(runoff,blur,scratch,width,.7f,token);
            bool rill=(iteration&1)==0;
            float rain=280*(preset=="desert"?.3f:1);
            for(int i=0;i<count;i++)
            {
                gx[i]=Derivative(height,i,width,cell,false);gz[i]=Derivative(height,i,width,cell,true);
                float s=MathF.Sqrt(gx[i]*gx[i]+gz[i]*gz[i]);slope[i]=s;
                float sine=s/MathF.Sqrt(1+s*s),cover=preset=="desert"?.7f:moist[i]>2.4f&&s<.35f?.3f:s>.5f?.5f:.6f;
                float a=blur[i]*cell;
                float capacity=soil[i]*cover*rain*(rill?MathF.Pow(a,1.6f)*MathF.Pow(sine,1.3f):a*sine*10);
                qx[i]=-gx[i]/Math.Max(s,1e-8f)*capacity;qz[i]=-gz[i]/Math.Max(s,1e-8f)*capacity;
                support[i]=stream[i]>=2&&accumulation[i]>12?1:0;
            }
            for(int i=0;i<count;i++)raw[i]=-Derivative(qx,i,width,cell,false)-Derivative(qz,i,width,cell,true);
            Blur(raw,change,scratch,width,.8f,token);
            for(int z=1;z<rows-1;z++)for(int x=1;x<width-1;x++)
            {
                int i=z*width+x;
                float delta=Math.Clamp(change[i]*.00038f*jitter[i],-.6f,.35f)*Math.Clamp(1-(filled[i]-height[i])/.5f,0,1);
                if(coastal&&height[i]<seaLevel)delta=0;
                height[i]+=delta;sediment[i]=Math.Max(0,sediment[i]+delta);
            }
            Blur(support,blur,scratch,width,1.5f,token);
            Array.Clear(raw);
            for(int i=0;i<count;i++)
            {
                int d=downstream[i];if(d<0)continue;
                float ds=Math.Max(0,(height[i]-height[d])/RegionalHydrology.Distance(i,d,width,cell));
                raw[i]=Math.Min(.9f,.025f*MathF.Sqrt(runoff[i])*ds)*Math.Clamp((runoff[i]-8)/12,0,1)*Math.Clamp(blur[i]*3,0,1);
            }
            Blur(raw,change,scratch,width,.8f,token);
            for(int z=1;z<rows-1;z++)for(int x=1;x<width-1;x++)
            {
                int i=z*width+x;
                float cut=Math.Min(change[i],Math.Max(0,height[i]-filled[i]+.8f));
                if(coastal&&height[i]<seaLevel)cut=0;
                height[i]-=cut;sediment[i]=Math.Max(0,sediment[i]-cut);
            }
            Array.Clear(change);
            for(int z=1;z<rows-1;z++)for(int x=1;x<width-1;x++)
            {
                int i=z*width+x;float mean=0;
                for(int dz=-1;dz<=1;dz++)for(int dx=-1;dx<=1;dx++)mean+=height[i+dz*width+dx]/9;
                change[i]=Math.Clamp(mean-height[i],-.3f,.3f)*Math.Clamp((slope[i]-.65f)*.25f,0,.25f);
            }
            for(int i=0;i<count;i++){height[i]+=change[i];sediment[i]=Math.Max(0,sediment[i]+change[i]);}
            OpenBoundary(height, width);
        }
        progress?.Invoke($"Erosion {Iterations}/{Iterations}");
    }
}
