// ---------------------------------------------------------------------------------------------
//  Copyright (c) 2021-2023, Jiaqi Liu. All rights reserved.
//  See LICENSE file in the project root for license information.
// ---------------------------------------------------------------------------------------------

namespace Pal3.State
{
    using System;
    using System.Collections.Generic;
    using Command;
    using Command.InternalCommands;
    using Command.SceCommands;
    using Input;
    using Script;
    using UnityEngine;

    public sealed class GameStateManager : IDisposable,
        ICommandExecutor<PlayerEnableInputCommand>,
        ICommandExecutor<GameStateChangeRequest>,
        ICommandExecutor<DialogueRenderingStartedNotification>,
        ICommandExecutor<ScriptFinishedRunningNotification>,
        ICommandExecutor<ScriptFailedToRunNotification>,
        ICommandExecutor<GameSwitchToMainMenuCommand>,
        ICommandExecutor<ResetGameStateCommand>
    {
        private GameState _previousState;
        private GameState _currentState;
        private readonly InputManager _inputManager;
        private readonly ScriptManager _scriptManager;
        private bool _isDebugging;
        private readonly HashSet<Guid> _gamePlayStateLockers = new ();

        private GameStateManager() { }

        public GameStateManager(InputManager inputManager, ScriptManager scriptManager)
        {
            _inputManager = inputManager ?? throw new ArgumentNullException(nameof(inputManager));
            _scriptManager = scriptManager ?? throw new ArgumentNullException(nameof(scriptManager));
            _previousState = GameState.UI;
            _currentState = GameState.UI;
            CommandExecutorRegistry<ICommand>.Instance.Register(this);
        }

        public void Dispose()
        {
            CommandExecutorRegistry<ICommand>.Instance.UnRegister(this);
        }

        public GameState GetCurrentState()
        {
            return _currentState;
        }

        /// <summary>
        /// Add a locker to prevent the game from entering GamePlay state.
        /// </summary>
        public void AddGamePlayStateLocker(Guid lockerId)
        {
            _gamePlayStateLockers.Add(lockerId);
        }

        public void RemoveGamePlayStateLocker(Guid lockerId)
        {
            _gamePlayStateLockers.Remove(lockerId);
        }

        public void GoToState(GameState newState)
        {
            if (_currentState == newState) return;

            if (newState == GameState.Gameplay)
            {
                if (_gamePlayStateLockers.Count > 0 ||
                    _scriptManager.GetNumberOfRunningScripts() > 0)
                {
                    // Do not allow to switch to Gameplay state if there are running scripts
                    // or there are GamePlay state lockers.
                    return;
                }
            }

            Debug.Log($"Goto State: {newState.ToString()}");

            _previousState = _currentState;
            _currentState = newState;

            UpdateInputManagerState();

            CommandDispatcher<ICommand>.Instance.Dispatch(
                new GameStateChangedNotification(_previousState, _currentState));
        }

        public void GoToPreviousState()
        {
            GoToState(_previousState);
        }

        private void UpdateInputManagerState()
        {
            if (!_isDebugging)
            {
                _inputManager.SwitchCurrentActionMap(_currentState);
            }
        }

        public void EnterDebugState()
        {
            _isDebugging = true;
            _inputManager.SwitchCurrentActionMap(GameState.UI);
        }

        public void LeaveDebugState()
        {
            _isDebugging = false;
            _inputManager.SwitchCurrentActionMap(_currentState);
        }

        public void Execute(PlayerEnableInputCommand command)
        {
            if (command.Enable == 0)
            {
                GoToState(GameState.Cutscene);
            }
            else if (command.Enable == 1)
            {
                GoToState(GameState.Gameplay);
            }
        }

        public void Execute(GameStateChangeRequest request)
        {
            GoToState(request.NewState);
        }

        public void Execute(DialogueRenderingStartedNotification command)
        {
            GoToState(GameState.Cutscene);
        }

        public void Execute(ScriptFinishedRunningNotification notification)
        {
            GoToState(GameState.Gameplay);
        }

        public void Execute(ScriptFailedToRunNotification notification)
        {
            GoToState(GameState.Gameplay);
        }

        public void Execute(GameSwitchToMainMenuCommand command)
        {
            CommandDispatcher<ICommand>.Instance.Dispatch(new ResetGameStateCommand());
        }

        public void Execute(ResetGameStateCommand command)
        {
            _gamePlayStateLockers.Clear();
        }
    }
}