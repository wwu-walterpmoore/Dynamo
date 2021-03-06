using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Linq;
using ProtoCore.AssociativeGraph;
using ProtoCore.AssociativeEngine;
using ProtoCore.AST;
using ProtoCore.AST.AssociativeAST;
using ProtoCore.BuildData;
using ProtoCore.CodeModel;
using ProtoCore.DebugServices;
using ProtoCore.DSASM;
using ProtoCore.Lang;
using ProtoCore.Lang.Replication;
using ProtoCore.Runtime;
using ProtoCore.Utils;
using ProtoFFI;

using StackFrame = ProtoCore.DSASM.StackFrame;

namespace ProtoCore
{
    public class InterpreterProperties
    {
        public GraphNode executingGraphNode { get; set; }
        public List<GraphNode> nodeIterations { get; set; }

        public List<StackValue> functionCallArguments { get; set; }
        public List<StackValue> functionCallDotCallDimensions { get; set; }

        public UpdateStatus updateStatus { get; set; }

        public InterpreterProperties()
        {
            Reset();
        }

        public InterpreterProperties(InterpreterProperties rhs)
        {
            executingGraphNode = rhs.executingGraphNode;
            nodeIterations = rhs.nodeIterations;
            functionCallArguments = rhs.functionCallArguments;
            functionCallDotCallDimensions = rhs.functionCallDotCallDimensions;
            updateStatus = rhs.updateStatus;
        }

        public void Reset()
        {
            executingGraphNode = null;
            nodeIterations = new List<GraphNode>();
            functionCallArguments = new List<StackValue>();
            functionCallDotCallDimensions = new List<StackValue>();
            updateStatus = UpdateStatus.kNormalUpdate;
        }
    }

    /// <summary>
    /// RuntimeCore is an object that is instantiated once across the lifecycle of the runtime
    /// This is the entry point of the runtime VM and its input is a DS Executable format. 
    /// There will only be one instance of RuntimeCore regardless of how many times instances of a DSASM.Executive (runtime VM) is instantiated.
    /// Its properties will be persistent and accessible across all instances of a DSASM.Executive
    /// </summary>
    public class RuntimeCore
    {
        public RuntimeCore(Heap heap)
        {
            // The heap is initialized by the core and is used to allocate strings
            // Use the that heap for runtime
            Validity.Assert(heap != null);
            this.Heap = heap;
            RuntimeMemory = new RuntimeMemory(Heap);

            InterpreterProps = new Stack<InterpreterProperties>();
            ReplicationGuides = new List<List<ReplicationGuide>>();

            RunningBlock = 0;
            ExecutionState = (int)ExecutionStateEventArgs.State.kInvalid; //not yet started
            FFIPropertyChangedMonitor = new FFIPropertyChangedMonitor(this);

            ContinuationStruct = new ContinuationStructure();


            watchStack = new List<StackValue>();
            watchFramePointer = Constants.kInvalidIndex;
            WatchSymbolList = new List<SymbolNode>();

            FunctionCallDepth = 0;
            cancellationPending = false;

            watchClassScope = Constants.kInvalidIndex;

            ExecutionInstance = CurrentExecutive = new Executive(this);
            ExecutiveProvider = new ExecutiveProvider();

            RuntimeStatus = new ProtoCore.RuntimeStatus(this);
        }

        public void SetProperties(Options runtimeOptions, Executable executable, DebugProperties debugProps = null, ProtoCore.Runtime.Context context = null, Executable exprInterpreterExe = null)
        {
            this.Context = context;
            this.DSExecutable = executable;
            this.Options = runtimeOptions;
            this.DebugProps = debugProps;
            this.ExprInterpreterExe = exprInterpreterExe;
        }

        public IExecutiveProvider ExecutiveProvider { get; set; }
        public Executive ExecutionInstance { get; private set; }
        public Executive CurrentExecutive { get; private set; }

        // Execution properties
        public Executable DSExecutable { get; private set; }
        public Executable ExprInterpreterExe { get; private set; }
        public Options Options { get; private set; }
        public RuntimeStatus RuntimeStatus { get; set; }
        public Stack<InterpreterProperties> InterpreterProps { get; set; }
        public ProtoCore.Runtime.Context Context { get; set; }

        // Memory
        public Heap Heap { get; set; }
        public RuntimeMemory RuntimeMemory { get; set; }

        public delegate void DisposeDelegate(RuntimeCore sender);
        public event DisposeDelegate Dispose;
        public event EventHandler<ExecutionStateEventArgs> ExecutionEvent;
        public int ExecutionState { get; set; }
        public FFIPropertyChangedMonitor FFIPropertyChangedMonitor { get; private set; }

        // this one is to address the issue that when the execution control is in a language block
        // which is further inside a function, the compiler feprun is false, 
        // when inspecting value in that language block or the function, debugger will assume the function index is -1, 
        // name look up will fail beacuse all the local variables inside 
        // that language block and fucntion has non-zero function index 
        public int FunctionCallDepth { get; set; }

        /// <summary>
        /// The currently executing blockID
        /// </summary>
        public int RunningBlock { get; set; }

        /// <summary>
        /// RuntimeExpressionUID is used by the associative engine at runtime to determine the current expression ID being executed
        /// </summary>
        public int RuntimeExpressionUID = 0;

        // Cached replication guides for the current call. 
        // TODO Jun: Store this in the dynamic table node
        public List<List<ReplicationGuide>> ReplicationGuides;

        private bool cancellationPending = false;
        public bool CancellationPending
        {
            get
            {
                return cancellationPending;
            }
        }

#region DEBUGGER_PROPERTIES

        public int watchClassScope { get; set; }

        public DebugProperties DebugProps { get; set; }
        public List<Instruction> Breakpoints { get; set; }

        // Continuation properties used for Serial mode execution and Debugging of Replicated calls
        public ContinuationStructure ContinuationStruct { get; set; }
        /// <summary>
        /// Gets the reason why the execution was last suspended
        /// </summary>
        public ReasonForExecutionSuspend ReasonForExecutionSuspend { get; internal set; }


        public List<StackValue> watchStack { get; set; }
        public int watchFramePointer { get; set; }

        public List<SymbolNode> WatchSymbolList { get; set; }
#endregion 
        
        public void ResetForDeltaExecution()
        {
            RunningBlock = 0;
            ExecutionState = (int)ExecutionStateEventArgs.State.kInvalid;
        }

        protected void OnDispose()
        {
            if (Dispose != null)
            {
                Dispose(this);
            }
        }

        public void Cleanup()
        {
            OnDispose();
            CLRModuleType.ClearTypes();
        }

        public void NotifyExecutionEvent(ExecutionStateEventArgs.State state)
        {
            switch (state)
            {
                case ExecutionStateEventArgs.State.kExecutionBegin:
                    Validity.Assert(ExecutionState == (int)ExecutionStateEventArgs.State.kInvalid, "Invalid Execution state being notified.");
                    break;
                case ExecutionStateEventArgs.State.kExecutionEnd:
                    if (ExecutionState == (int)ExecutionStateEventArgs.State.kInvalid) //execution never begun.
                        return;
                    break;
                case ExecutionStateEventArgs.State.kExecutionBreak:
                    Validity.Assert(ExecutionState == (int)ExecutionStateEventArgs.State.kExecutionBegin || ExecutionState == (int)ExecutionStateEventArgs.State.kExecutionResume, "Invalid Execution state being notified.");
                    break;
                case ExecutionStateEventArgs.State.kExecutionResume:
                    Validity.Assert(ExecutionState == (int)ExecutionStateEventArgs.State.kExecutionBreak, "Invalid Execution state being notified.");
                    break;
                default:
                    Validity.Assert(false, "Invalid Execution state being notified.");
                    break;
            }
            ExecutionState = (int)state;
            if (null != ExecutionEvent)
                ExecutionEvent(this, new ExecutionStateEventArgs(state));
        }
        
        public bool IsEvalutingPropertyChanged()
        {
            foreach (var prop in InterpreterProps)
            {
                if (prop.updateStatus == UpdateStatus.kPropertyChangedUpdate)
                {
                    return true;
                }
            }

            return false;
        }

        public void RequestCancellation()
        {
            if (cancellationPending)
            {
                var message = "Cancellation cannot be requested twice";
                throw new InvalidOperationException(message);
            }

            cancellationPending = true;
        }

        public int GetCurrentBlockId()
        {
            int constructBlockId = RuntimeMemory.CurrentConstructBlockId;
            if (constructBlockId == Constants.kInvalidIndex)
                return DebugProps.CurrentBlockId;

            CodeBlock constructBlock = ProtoCore.Utils.CoreUtils.GetCodeBlock(DSExecutable.CodeBlocks, constructBlockId);
            while (null != constructBlock && constructBlock.blockType == CodeBlockType.kConstruct)
            {
                constructBlock = constructBlock.parent;
            }

            if (null != constructBlock)
                constructBlockId = constructBlock.codeBlockId;

            if (constructBlockId != DebugProps.CurrentBlockId)
                return DebugProps.CurrentBlockId;
            else
                return RuntimeMemory.CurrentConstructBlockId;
        }

        //STop
        public Stopwatch StopWatch;
        public void StartTimer()
        {
            StopWatch = new Stopwatch();
            StopWatch.Start();
        }
        public TimeSpan GetCurrentTime()
        {
            TimeSpan ts = StopWatch.Elapsed;
            return ts;
        }

    }
}
