using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Threading;
using CapFrameX.PresentMonInterface;
using CapFrameX.View;
using Prism.Ioc;
using Prism.Modularity;
using Serilog;

namespace CapFrameX
{
    public class CapFrameXViewRegion : IModule
    {
        public void RegisterTypes(IContainerRegistry containerRegistry)
        {
        }

        public void OnInitialized(IContainerProvider containerProvider)
        {
            using (StartupPerformanceLogger.Measure("CapFrameX view module initialization total"))
            {
                RegisterViewWithTiming("ColorbarRegion", typeof(ColorbarView));
                RegisterViewWithTiming("ControlRegion", typeof(ControlView));

                bool isCaptureServiceCompatible;
                using (StartupPerformanceLogger.Measure("PresentMon OS compatibility check"))
                {
                    isCaptureServiceCompatible = CaptureServiceInfo.IsCompatibleWithRunningOS;
                }

                // First DataRegion registration = startup view; must match the
                // ColorbarViewModel default (InfoIsChecked).
                RegisterViewWithTiming("DataRegion", typeof(InfoView));
                RegisterViewWithTiming("StateRegion", typeof(StateView));

                // Capture and Overlay must exist before the shell becomes interactive.
                // Deferring these to ApplicationIdle can leave CAPTURE navigating to
                // a view that has not been registered yet.
                if (isCaptureServiceCompatible)
                    RegisterViewWithTiming("DataRegion", typeof(CaptureView));
                RegisterViewWithTiming("DataRegion", typeof(OverlayView));

                // Keep Pass 13's startup optimization for non-critical tabs.
                var deferredViews = new List<Type>();
                deferredViews.Add(typeof(DataView));
                deferredViews.Add(typeof(AggregationView));
                deferredViews.Add(typeof(ComparisonView));
                deferredViews.Add(typeof(SensorView));
                deferredViews.Add(typeof(PmdView));
                deferredViews.Add(typeof(ReportView));
                deferredViews.Add(typeof(SynchronizationView));
                deferredViews.Add(typeof(CloudView));

                RegisterDeferred("DataRegion", deferredViews);
            }
        }

        /// <summary>
        /// Queues one registration per dispatcher idle turn.
        /// </summary>
        private static void RegisterDeferred(string regionName, IReadOnlyList<Type> viewTypes)
        {
            var dispatcher = Application.Current?.Dispatcher;

            if (dispatcher == null)
            {
                foreach (var viewType in viewTypes)
                    RegisterViewWithTiming(regionName, viewType);

                return;
            }

            RegisterNextDeferred(dispatcher, regionName, viewTypes, 0);
        }

        private static void RegisterNextDeferred(Dispatcher dispatcher, string regionName,
            IReadOnlyList<Type> viewTypes, int index)
        {
            if (index >= viewTypes.Count)
            {
                StartupPerformanceLogger.Mark("Deferred tab registration drained");
                return;
            }

            dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    RegisterViewWithTiming(regionName, viewTypes[index]);
                }
                catch (Exception ex)
                {
                    Log.Logger.Error(ex, "Error while registering {viewName} with {regionName}.",
                        viewTypes[index].Name, regionName);
                }
                finally
                {
                    RegisterNextDeferred(dispatcher, regionName, viewTypes, index + 1);
                }
            }), DispatcherPriority.ApplicationIdle);
        }

        private static void RegisterViewWithTiming(string regionName, Type viewType)
        {
            using (StartupPerformanceLogger.Measure("Region registration/activation: " + viewType.Name))
            {
                RegionManagerWrapper.Singleton.RegisterViewWithRegion(regionName, viewType);
            }
        }
    }
}
