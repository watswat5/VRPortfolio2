using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;
using WifiHeatmap.Core;
using WifiHeatmap.Heatmap;
using NumericsVector = System.Numerics.Vector3;

namespace WifiHeatmap.Preview
{
    /// <summary>One bounded ray-marched 3D texture for the miniature only. Builds on a worker;
    /// Unity objects, anchor resolution, uploads and material changes stay on the main thread.</summary>
    public sealed class MiniatureFog : IDisposable
    {
        private sealed class BuildResult
        {
            public FogField.Field Field;
            public Color[] Pixels;
            public Bounds Bounds;
            public ulong Hash;
            public double Milliseconds;
            public int Generation;
        }
        private readonly GameObject box;
        private readonly Material material;
        private readonly List<FogField.Point> points = new List<FogField.Point>();
        private Texture3D texture;
        private Task<BuildResult> work;
        private CancellationTokenSource cancellation;
        private Transform roomContext;
        private object datasetContext;
        private int generation;
        private bool hasHash, ready, supported;
        private ulong appliedHash;
        private float nextBuild;
        public string Status { get; private set; }
        public MiniatureFog(Transform parent, Material sourceMaterial)
        {
            supported=SystemInfo.supports3DTextures && SystemInfo.SupportsTextureFormat(TextureFormat.RGBAHalf) &&
                sourceMaterial != null && sourceMaterial.shader.isSupported;
            Status=supported ? "Fog: waiting for localized measurements." : "Fog unavailable on this graphics device. Choose Voxels.";
            if (!supported) return;
            material=new Material(sourceMaterial) { name="Miniature fog (runtime)" };
            box=GameObject.CreatePrimitive(PrimitiveType.Cube); box.name="Miniature fog volume";
            box.transform.SetParent(parent,false);
            UnityEngine.Object.Destroy(box.GetComponent<Collider>());
            var renderer=box.GetComponent<MeshRenderer>(); renderer.sharedMaterial=material;
            renderer.shadowCastingMode=ShadowCastingMode.Off; renderer.receiveShadows=false;
            box.SetActive(false);
        }
        public void Refresh(IReadOnlyList<VoxelCloudLayer.CloudCell> cells, object dataset, Transform room, Bounds bounds,
            CloudSettings settings, int steps, float density)
        {
            if (!supported) return;
            if (!ReferenceEquals(datasetContext,dataset) || roomContext != room)
            {
                Invalidate(); datasetContext=dataset; roomContext=room;
            }
            SetMaterial(settings,steps,density);
            if (cells.Count==0 || dataset==null)
            { Invalidate(); Status="Fog: no accepted, localized cells in this dataset."; return; }
            if (work != null && work.IsCompleted)
            {
                try
                {
                    BuildResult result=work.GetAwaiter().GetResult();
                    if (result.Generation==generation) Apply(result);
                }
                catch (OperationCanceledException) { }
                catch (Exception exception)
                { ready=false; Status=$"Fog build failed: {exception.Message}. Choose Voxels."; }
                finally { work=null; cancellation?.Dispose(); cancellation=null; }
            }
            box.SetActive(ready);
            if (work!=null || Time.unscaledTime < nextBuild) return;
            nextBuild=Time.unscaledTime+.2f;
            if (room==null || bounds.size.x<=0 || bounds.size.y<=0 || bounds.size.z<=0)
            { Invalidate(); Status="Fog: room has no usable volume bounds."; return; }
            points.Clear();
            ulong hash=14695981039346656037UL;
            Hash(ref hash,bounds.center); Hash(ref hash,bounds.size); Hash(ref hash,settings.Resolution);
            foreach (var cell in cells)
            {
                if (cell.Anchor==null) continue;
                Vector3 position=room.InverseTransformPoint(cell.Anchor.TransformPoint(cell.Center));
                if (!bounds.Contains(position)) continue;
                points.Add(new FogField.Point(new NumericsVector(position.x,position.y,position.z),cell.Dbm,cell.Confidence));
                Hash(ref hash,position); Hash(ref hash,cell.Dbm); Hash(ref hash,cell.Confidence);
            }
            if (points.Count==0)
            { Invalidate(); Status="Fog: no measured cells inside this room's bounds."; return; }
            if (hasHash && hash==appliedHash) return;
            FogField.Point[] snapshot=points.ToArray();
            var minimum=new NumericsVector(bounds.min.x,bounds.min.y,bounds.min.z);
            var size=new NumericsVector(bounds.size.x,bounds.size.y,bounds.size.z);
            float resolution=settings.Resolution;
            int buildGeneration=generation;
            cancellation=new CancellationTokenSource();
            CancellationToken token=cancellation.Token;
            work=Task.Run(() => Build(snapshot,minimum,size,resolution,bounds,hash,buildGeneration,token),token);
            if (!ready) Status="Fog: building measured coverage...";
        }
        private static BuildResult Build(FogField.Point[] snapshot,NumericsVector minimum,NumericsVector size,
            float resolution,Bounds bounds,ulong hash,int generation,CancellationToken token)
        {
            var timer=Stopwatch.StartNew();
            FogField.Field field=FogField.Build(snapshot,minimum,size,resolution,token);
            var pixels=new Color[field.Width*field.Height*field.Depth];
            for (int i=0;i<pixels.Length;i++)
            {
                if ((i&4095)==0) token.ThrowIfCancellationRequested();
                pixels[i]=new Color(field.Channels[i*4],field.Channels[i*4+1],field.Channels[i*4+2],0);
            }
            // No Unity object APIs are called here; Color and Bounds are value snapshots only.
            field.Channels=null;
            return new BuildResult { Field=field, Pixels=pixels, Bounds=bounds, Hash=hash,
                Milliseconds=timer.Elapsed.TotalMilliseconds, Generation=generation };
        }
        private void Apply(BuildResult result)
        {
            var field=result.Field;
            if (texture==null || texture.width!=field.Width || texture.height!=field.Height || texture.depth!=field.Depth)
            {
                if (texture!=null) UnityEngine.Object.Destroy(texture);
                texture=new Texture3D(field.Width,field.Height,field.Depth,TextureFormat.RGBAHalf,false)
                { name="Measured miniature RSSI/coverage", wrapMode=TextureWrapMode.Clamp, filterMode=FilterMode.Bilinear };
            }
            texture.SetPixels(result.Pixels); texture.Apply(false,false);
            material.SetTexture("_Field",texture);
            material.SetVector("_RoomSize",result.Bounds.size);
            box.transform.localPosition=result.Bounds.center; box.transform.localScale=result.Bounds.size;
            ready=hasHash=true; appliedHash=result.Hash;
            Status=$"Fog: {field.Width}x{field.Height}x{field.Depth}; support {field.SupportRadius*100:0} cm; build {result.Milliseconds:0.0} ms (worker).";
        }
        public void SetSignalDensity(bool enabled,float amount)
        {
            if (material != null) material.SetFloat("_SignalDensityInfluence",enabled ? Mathf.Clamp01(amount) : 0);
        }
        private void SetMaterial(CloudSettings settings,int steps,float density)
        {
            // The fog's density is the preview's own (RoomPreview.FogDensity); the Cloud tab's opacity is the full-size cloud's.
            material.SetInteger("_Steps",steps); material.SetFloat("_Opacity",density);
            material.SetVector("_SignalRange",new Vector4(settings.LowColorDbm,settings.HighColorDbm,0,0));
            // Normalize density against the full display endpoints, independently of gradient stop handles.
            material.SetVector("_DensitySignalRange",new Vector4(settings.LowDbm,settings.HighDbm,0,0));
            material.SetColor("_LowColor",settings.GetColor(0)); material.SetColor("_MidColor",settings.GetColor(1));
            material.SetColor("_HighColor",settings.GetColor(2));
        }
        private static void Hash(ref ulong hash,Vector3 value)
        { Hash(ref hash,value.x); Hash(ref hash,value.y); Hash(ref hash,value.z); }
        private static void Hash(ref ulong hash,float value)
        { unchecked { hash=(hash^(uint)Mathf.RoundToInt(value*1000))*1099511628211UL; } }
        private void Invalidate()
        {
            generation++; ready=hasHash=false; nextBuild=0; cancellation?.Cancel();
            if (box!=null) box.SetActive(false);
        }
        public void Suspend() { Invalidate(); }
        public void Hide() { if (box!=null) box.SetActive(false); }
        public void Dispose()
        {
            cancellation?.Cancel(); cancellation?.Dispose();
            // Observe faults even if the component is destroyed before the next refresh.
            if (work!=null) _=work.ContinueWith(t => { _=t.Exception; },TaskContinuationOptions.OnlyOnFaulted);
            if (box!=null) UnityEngine.Object.Destroy(box);
            if (texture!=null) UnityEngine.Object.Destroy(texture);
            if (material!=null) UnityEngine.Object.Destroy(material);
        }
    }
}
