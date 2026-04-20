using System;
using System.Collections.Generic;

namespace RedeiptEditor.NetSolution
{
    /// <summary>
    /// 简单状态机框架：
    /// - 有状态机名字
    /// - 每个状态支持 Enter/Exit/Run 三个回调
    /// - 可切换状态、按需驱动 Run
    /// 
    /// 说明：这是框架，具体业务逻辑由外部在回调里实现。
    /// </summary>
    public sealed class SimpleStateMachine
    {
        public sealed class State
        {
            public string Name { get; }

            /// <summary>进入状态时回调（从其它状态切入后调用）。</summary>
            public Action<SimpleStateMachine> OnEnter { get; }

            /// <summary>退出状态时回调（切到其它状态前调用）。</summary>
            public Action<SimpleStateMachine> OnExit { get; }

            /// <summary>状态运行回调（由外部周期性调用 <see cref="SimpleStateMachine.Tick"/> 驱动）。</summary>
            public Action<SimpleStateMachine> OnRun { get; }

            public State(
                string name,
                Action<SimpleStateMachine> onEnter = null,
                Action<SimpleStateMachine> onExit = null,
                Action<SimpleStateMachine> onRun = null)
            {
                if (string.IsNullOrWhiteSpace(name))
                    throw new ArgumentException("状态名不能为空", nameof(name));

                Name = name;
                OnEnter = onEnter;
                OnExit = onExit;
                OnRun = onRun;
            }

            public override string ToString() => Name;
        }

        public string MachineName { get; }

        /// <summary>当前状态（未启动前可能为 null）。</summary>
        public State Current { get; private set; }

        /// <summary>上一状态（未切换前可能为 null）。</summary>
        public State Previous { get; private set; }

        /// <summary>从启动到当前的 Tick 次数（用于调试/统计）。</summary>
        public long TickCount { get; private set; }

        private readonly Dictionary<string, State> _statesByName =
            new Dictionary<string, State>(StringComparer.OrdinalIgnoreCase);

        public SimpleStateMachine(string machineName)
        {
            if (string.IsNullOrWhiteSpace(machineName))
                throw new ArgumentException("状态机名字不能为空", nameof(machineName));
            MachineName = machineName;
        }

        /// <summary>注册一个状态。若同名已存在则抛异常。</summary>
        public void AddState(State state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (_statesByName.ContainsKey(state.Name))
                throw new InvalidOperationException($"状态 '{state.Name}' 已存在，不能重复添加。");
            _statesByName[state.Name] = state;
        }

        /// <summary>按名字获取状态（不存在返回 null）。</summary>
        public State GetState(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            _statesByName.TryGetValue(name, out var s);
            return s;
        }

        /// <summary>
        /// 启动状态机：设置初始状态并调用 Enter。
        /// </summary>
        public void Start(State initialState)
        {
            if (initialState == null) throw new ArgumentNullException(nameof(initialState));
            if (Current != null)
                throw new InvalidOperationException("状态机已启动，不能重复 Start。");

            if (GetState(initialState.Name) == null)
                AddState(initialState);

            Previous = null;
            Current = initialState;
            TickCount = 0;
            SafeInvoke(Current.OnEnter);
        }

        /// <summary>
        /// 切换状态：调用旧状态 Exit，再调用新状态 Enter。
        /// </summary>
        public void TransitionTo(State next)
        {
            if (next == null) throw new ArgumentNullException(nameof(next));
            if (Current == null)
                throw new InvalidOperationException("状态机尚未 Start，不能切换状态。");

            if (ReferenceEquals(Current, next))
                return;

            if (GetState(next.Name) == null)
                AddState(next);

            var old = Current;
            SafeInvoke(old.OnExit);
            Previous = old;
            Current = next;
            SafeInvoke(Current.OnEnter);
        }

        /// <summary>
        /// 周期驱动：调用当前状态 Run。
        /// </summary>
        public void Tick()
        {
            if (Current == null)
                throw new InvalidOperationException("状态机尚未 Start，不能 Tick。");

            TickCount++;
            SafeInvoke(Current.OnRun);
        }

        private void SafeInvoke(Action<SimpleStateMachine> cb)
        {
            cb?.Invoke(this);
        }
    }
}

