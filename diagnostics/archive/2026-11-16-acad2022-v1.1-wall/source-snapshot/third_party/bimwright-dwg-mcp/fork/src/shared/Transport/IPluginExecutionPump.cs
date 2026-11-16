// Modified for CAD Agent Foundation by capskov (2026); derived from Bimwright DWG MCP pinned at 4d0f75646631186612af926944d7e33affc4757d. See NOTICE.md.
//
// The execution queue is drained on AutoCAD's Idle thread. Upstream only reached
// the pump through PipeTransportServer, which coupled "the transport is a pipe"
// with "the queue may run". AutoCAD 2022 runs TCP, so the pump never fired there
// and every job stayed queued. This interface separates that capability from
// the transport kind: any transport that can pump a job declares it here, and
// the Idle hook asks for the capability instead of for a concrete type.
#if ACAD2026
using Bimwright.Dwg.Plugin.Execution;

namespace Bimwright.Dwg.Plugin
{
    /// <summary>Implemented by transports that can drain one queued job from Idle.</summary>
    internal interface IPluginExecutionPump
    {
        /// <summary>
        /// Runs at most one queued job on AutoCAD's main thread. Returns null when
        /// nothing ran (transport stopped, queue empty, or another pump is active).
        /// </summary>
        PluginExecutionQueuePumpResult PumpOneQueueJob();
    }
}
#endif
