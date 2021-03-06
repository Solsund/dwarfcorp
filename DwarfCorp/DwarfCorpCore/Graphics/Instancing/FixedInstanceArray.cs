﻿using System;
using System.Collections.Generic;
using System.Linq;
using DwarfCorpCore;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System.Threading;
using Newtonsoft.Json;

namespace DwarfCorp
{
    /// <summary>
    /// A fixed instance array draws the closest K instances of a model. 
    /// It constantly sorts and frustrum culls instances en masse.
    /// </summary>
    [JsonObject(IsReference = true)]
    public class FixedInstanceArray
    {
        private MinBag<InstanceData> SortedData { get; set; }
        private List<InstanceData> Data { get; set; }
        private List<InstanceData> Additions { get; set; }
        private List<InstanceData> Removals { get; set; }
        private int numInstances = 0;
        private int numActiveInstances = 0;
        public int NumInstances
        {
            get { return numInstances; }
            set { SetNumInstances(value); }
        }

        [JsonIgnore]
        public GeometricPrimitive Model { get; set; }
        public Texture2D Texture { get; set; }
        public bool ShouldRebuild { get; set; }
        public string Name { get; set; }
        private Mutex DataLock { get; set; }

        private static RasterizerState rasterState = new RasterizerState()
        {
            CullMode = CullMode.None,
        };

        public Camera Camera;

        public BlendState BlendMode { get; set; }
        public float CullDistance = 100 * 100;

        private DynamicVertexBuffer instanceBuffer;
        private InstancedVertex[] instanceVertexes;

        public void Clear()
        {
            Data.Clear();
            SortedData.Clear();
        }

        public void CreateDepths(Camera inputCamera)
        {
            if (inputCamera == null)
            {
                return;
            }

            BoundingFrustum frust = inputCamera.GetFrustrum();
            BoundingBox box = MathFunctions.GetBoundingBox(frust.GetCorners());
            Vector3 forward = frust.Near.Normal;

            Vector3 z = Vector3.Zero;
            foreach (InstanceData instance in Data)
            {
                z = instance.Transform.Translation - inputCamera.Position;
                instance.Depth = z.LengthSquared();

                if (instance.Depth < CullDistance)
                {

                    // Half plane test. Faster. Much less accurate.
                    //if (Vector3.Dot(z, forward) > 0)
                    if(box.Contains(instance.Transform.Translation) != ContainmentType.Contains)
                    {
                        instance.Depth *= 100;
                    }
                }
                else
                {
                    instance.Depth *= 100;
                }
            }
        }

        private Timer sortTimer = new Timer(0.1f, false);

        public void SortDistances()
        {
            CreateDepths(Camera);

            SortedData.Clear();
            foreach (InstanceData t in Data.Where(t => t.Depth < CullDistance))
            {
                SortedData.Add(t, t.Depth);
            }
        }



        public FixedInstanceArray()
        {
            Data = new List<InstanceData>();
            Additions = new List<InstanceData>();
            Removals = new List<InstanceData>();
        }

        public FixedInstanceArray(string name, GeometricPrimitive model, Texture2D texture, int numInstances, BlendState blendMode)
        {
            CullDistance = (GameSettings.Default.ChunkDrawDistance * GameSettings.Default.ChunkDrawDistance) - 40;
            Name = name;
            Model = model;
            Data = new List<InstanceData>();
            Additions = new List<InstanceData>();
            Removals = new List<InstanceData>();

            SortedData = new MinBag<InstanceData>(numInstances);
            NumInstances = numInstances;

            ShouldRebuild = true;
            Texture = texture;
            DataLock = new Mutex();

            BlendMode = blendMode;
        }


        public void DeleteNulls()
        {
            for (int j = 0; j < Data.Count; j++)
            {
                if (Data[j] == null)
                {
                    Data.RemoveAt(j);
                    j--;
                }
            }
        }

        public void Update(DwarfTime time, Camera cam, GraphicsDevice graphics)
        {
            if (DwarfGame.ExitGame)
            {
                return;
            }

            DeleteNulls();
            sortTimer.Update(time);

            if (sortTimer.HasTriggered)
            {
                SortDistances();
                sortTimer.Reset(sortTimer.TargetTimeSeconds);
            }

            AddRemove();


            if (instanceVertexes == null)
            {
                instanceVertexes = new InstancedVertex[numInstances];
            }
            int j = 0;
            foreach (InstanceData t in SortedData.Data)
            {
                if (t.ShouldDraw)
                {
                    instanceVertexes[j].Transform = t.Transform;
                    instanceVertexes[j].Color = t.Color;
                    j++;
                }
            }
            numActiveInstances = j;
        }

        public void Render(GraphicsDevice graphics, Effect effect, Camera cam, bool rebuildVertices)
        {
            Camera = cam;

            if (instanceBuffer == null)
            {
                instanceBuffer = new DynamicVertexBuffer(graphics, InstancedVertex.VertexDeclaration, numInstances,
                    BufferUsage.None);
            }

            if (SortedData.Data.Count > 0 && numActiveInstances > 0)
            {
                graphics.RasterizerState = rasterState;

                effect.CurrentTechnique = effect.Techniques["Instanced"];
                effect.Parameters["xEnableLighting"].SetValue(1);

                if (Model.VertexBuffer == null || Model.IndexBuffer == null)
                {
                    Model.ResetBuffer(graphics);
                }

                instanceBuffer.SetData(instanceVertexes, 0, SortedData.Data.Count, SetDataOptions.Discard);

                graphics.SetVertexBuffers(Model.VertexBuffer, new VertexBufferBinding(instanceBuffer, 0, 1));

                bool hasIndex = Model.IndexBuffer != null;
                graphics.Indices = Model.IndexBuffer;

                BlendState blendState = graphics.BlendState;
                graphics.BlendState = BlendMode;

                effect.Parameters["xTexture"].SetValue(Texture);
                effect.Parameters["xTint"].SetValue(Vector4.One);
                foreach (EffectPass pass in effect.CurrentTechnique.Passes)
                {
                    pass.Apply();
                    graphics.DrawInstancedPrimitives(PrimitiveType.TriangleList, 0, 0,
                                        Model.MaxVertex, 0,
                                        Model.Indexes.Length / 3,
                                        numActiveInstances);
                    /*
                    foreach (InstanceData instance in SortedData.Data)
                    {
                        if (!instance.ShouldDraw) continue;

                        if (!hasIndex)
                        {
                            graphics.DrawPrimitives(PrimitiveType.TriangleList, 0, Model.VertexBuffer.VertexCount/3);
                        }
                        else
                        {
                            graphics.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0,
                                Model.VertexBuffer.VertexCount, 0, Model.IndexBuffer.IndexCount/3);
                        }
                    }
                     */
                }

                effect.CurrentTechnique = effect.Techniques["Textured"];
                effect.Parameters["xWorld"].SetValue(Matrix.Identity);
                graphics.BlendState = blendState;

            }
        }

        public void Add(InstanceData data)
        {
            DataLock.WaitOne();
            Additions.Add(data);
            DataLock.ReleaseMutex();
        }

        public void Remove(InstanceData data)
        {
            DataLock.WaitOne();
            Removals.Add(data);
            DataLock.ReleaseMutex();
        }

        private void AddRemove()
        {
            DataLock.WaitOne();
            foreach (InstanceData t in Additions)
            {
                Data.Add(t);
            }

            foreach (InstanceData t in Removals)
            {
                Data.Remove(t);
            }

            Additions.Clear();
            Removals.Clear();
            DataLock.ReleaseMutex();
        }

        public void SetNumInstances(int nInstances)
        {
            numInstances = nInstances;
        }
    }

}