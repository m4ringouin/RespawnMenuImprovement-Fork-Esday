using System;
using System.Reflection;
using VRage.Plugins;
using HarmonyLib;

namespace RespawnMenuImprovements;

public class Main : IPlugin
{
    public void Init(object gameInstance)
    {
        new Harmony("RespawnMenuImprovements").PatchAll(Assembly.GetExecutingAssembly());
    }

    public void Update()
    {
        Patches.Update();
    }

    public void Dispose()
    {

    }
}