﻿// ---------------------------------------------------------------------------------------------
//  Copyright (c) 2021-2023, Jiaqi Liu. All rights reserved.
//  See LICENSE file in the project root for license information.
// ---------------------------------------------------------------------------------------------

namespace Pal3.Camera
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using Command;
    using Command.InternalCommands;
    using Command.SceCommands;
    using Core.Animation;
    using Core.DataReader.Scn;
    using Core.GameBox;
    using Core.Utils;
    using Input;
    using MetaData;
    using Player;
    using Scene;
    using Script.Waiter;
    using State;
    using UnityEngine;
    using UnityEngine.InputSystem.OnScreen;
    using UnityEngine.UI;

    public sealed class CameraManager : MonoBehaviour,
        ICommandExecutor<CameraSetTransformCommand>,
        ICommandExecutor<CameraSetDefaultTransformCommand>,
        ICommandExecutor<CameraShakeEffectCommand>,
        ICommandExecutor<CameraOrbitCommand>,
        ICommandExecutor<CameraRotateCommand>,
        #if PAL3A
        ICommandExecutor<CameraOrbitHorizontalCommand>,
        ICommandExecutor<CameraOrbitVerticalCommand>,
        #endif
        ICommandExecutor<CameraFadeInCommand>,
        ICommandExecutor<CameraFadeInWhiteCommand>,
        ICommandExecutor<CameraFadeOutCommand>,
        ICommandExecutor<CameraFadeOutWhiteCommand>,
        ICommandExecutor<CameraPushCommand>,
        ICommandExecutor<CameraMoveCommand>,
        ICommandExecutor<CameraFreeCommand>,
        ICommandExecutor<CameraSetYawCommand>,
        ICommandExecutor<CameraFocusOnActorCommand>,
        ICommandExecutor<CameraFocusOnSceneObjectCommand>,
        ICommandExecutor<ScenePreLoadingNotification>,
        ICommandExecutor<SceneLeavingCurrentSceneNotification>,
        ICommandExecutor<GameStateChangedNotification>,
        ICommandExecutor<ResetGameStateCommand>
    {
        private const float FADE_ANIMATION_DURATION = 3f;
        private const float SCENE_STORY_B_ROOM_FLOOR_HEIGHT = 1.6f;
        private const float CAMERA_DEFAULT_DISTANCE = 46f;
        private const float CAMERA_SCENE_STORY_B_DISTANCE = 34f;
        private const float CAMERA_ROTATION_SPEED_KEY_PRESS = 120f;
        private const float CAMERA_ROTATION_SPEED_SCROLL = 15f;
        private const float CAMERA_ROTATION_SPEED_DRAG = 10f;
        private const float CAMERA_SMOOTH_FOLLOW_TIME = 0.2f;
        private const float CAMERA_SMOOTH_FOLLOW_MAX_DISTANCE = 3f;

        private Camera _camera;
        private Image _curtainImage;
        private Vector3 _lastLookAtPoint = Vector3.zero;
        private Vector3 _cameraMoveVelocity = Vector3.zero;

        private bool _shouldResetVelocity = false;

        private Vector3 _actualPosition = Vector3.zero;
        private Vector3 _actualLookAtPoint = Vector3.zero;

        private Vector3 _cameraOffset = Vector3.zero;
        private float _lookAtPointYOffset;

        private bool _free = true;

        private PlayerInputActions _inputActions;
        private PlayerGamePlayController _gamePlayController;
        private SceneManager _sceneManager;
        private GameStateManager _gameStateManager;
        private bool _freeToRotate;

        private GameObject _lookAtGameObject;

        private int _currentAppliedDefaultTransformOption = 0;

        private RectTransform _joyStickRect;
        private float _joyStickMovementRange;
        private bool _isTouchEnabled;

        private bool _cameraAnimationInProgress;
        private CancellationTokenSource _asyncCameraAnimationCts = new ();
        private CancellationTokenSource _cameraFadeAnimationCts = new ();

        private const int LAST_KNOWN_SCENE_STATE_LIST_MAX_LENGTH = 1;
        private readonly List<(ScnSceneInfo sceneInfo,
            Vector3 cameraPosition,
            Quaternion cameraRotation,
            Vector3 cameraOffset)> _cameraLastKnownSceneState = new ();

        public void Init(PlayerInputActions inputActions,
            PlayerGamePlayController gamePlayController,
            SceneManager sceneManager,
            GameStateManager gameStateManager,
            Camera mainCamera,
            Canvas touchControlUI,
            Image curtainImage)
        {
            _inputActions = inputActions ?? throw new ArgumentNullException(nameof(inputActions));
            _gamePlayController = gamePlayController != null ? gamePlayController : throw new ArgumentNullException(nameof(gamePlayController));
            _sceneManager = sceneManager ?? throw new ArgumentNullException(nameof(sceneManager));
            _gameStateManager = gameStateManager ?? throw new ArgumentNullException(nameof(gameStateManager));

            _camera = mainCamera;
            _camera!.fieldOfView = HorizontalToVerticalFov(24.05f, 4f/3f);

            _curtainImage = curtainImage;

            _isTouchEnabled = Utility.IsHandheldDevice();
            var onScreenStick = touchControlUI.GetComponentInChildren<OnScreenStick>();
            _joyStickMovementRange = onScreenStick.movementRange;
            var joyStickImage = onScreenStick.gameObject.GetComponent<Image>();
            _joyStickRect = joyStickImage.rectTransform;
        }

        private void OnEnable()
        {
            CommandExecutorRegistry<ICommand>.Instance.Register(this);
        }

        private void OnDisable()
        {
            CommandExecutorRegistry<ICommand>.Instance.UnRegister(this);
        }

        private void LateUpdate()
        {
            if (!_free)
            {
                _shouldResetVelocity = true;
                return;
            }

            if (_cameraAnimationInProgress) return;

            if (_lookAtGameObject != null)
            {
                _lastLookAtPoint = _lookAtGameObject.transform.position;
            }
            else if (_gamePlayController.TryGetPlayerActorLastKnownPosition(out Vector3 playerActorPosition))
            {
                _lastLookAtPoint = playerActorPosition;
            }

            var yOffset = new Vector3(0f, _lookAtPointYOffset, 0f);
            Vector3 targetPosition = _lastLookAtPoint + _cameraOffset;
            Vector3 currentPosition = _camera.transform.position;

            Vector3 previousLookAtPoint = _lastLookAtPoint + (currentPosition - targetPosition);

            if (Vector3.Distance(targetPosition, currentPosition) > CAMERA_SMOOTH_FOLLOW_MAX_DISTANCE ||
                _shouldResetVelocity)
            {
                _actualPosition = targetPosition;
                _actualLookAtPoint = _lastLookAtPoint;
                if (_shouldResetVelocity)
                {
                    _cameraMoveVelocity = Vector3.zero;
                    _shouldResetVelocity = false;
                }
            }
            else
            {
                _actualPosition = Vector3.SmoothDamp(currentPosition,
                    targetPosition, ref _cameraMoveVelocity, CAMERA_SMOOTH_FOLLOW_TIME);
                _actualLookAtPoint = previousLookAtPoint + (_actualPosition - currentPosition);
            }

            _camera.transform.position = _actualPosition;

            if (_lookAtGameObject != null) return;

            if (!_freeToRotate)
            {
                _camera.transform.LookAt(_actualLookAtPoint + yOffset);
                return;
            }

            RotateCameraBasedOnUserInput();
        }

        private void RotateCameraBasedOnUserInput()
        {
            if (_inputActions.Gameplay.RotateCameraClockwise.inProgress)
            {
                RotateToOrbitPoint(Time.deltaTime * CAMERA_ROTATION_SPEED_KEY_PRESS);
            }
            else if (_inputActions.Gameplay.RotateCameraCounterClockwise.inProgress)
            {
                RotateToOrbitPoint(-Time.deltaTime * CAMERA_ROTATION_SPEED_KEY_PRESS);
            }

            var mouseScroll = _inputActions.Gameplay.OnScroll.ReadValue<float>();
            if (mouseScroll != 0)
            {
                RotateToOrbitPoint(mouseScroll * Time.deltaTime * CAMERA_ROTATION_SPEED_SCROLL);
            }

            if (!_isTouchEnabled) return;

            var touch0Delta = _inputActions.Gameplay.Touch0Delta.ReadValue<float>();
            if (touch0Delta != 0)
            {
                var touchStartPosition = _inputActions.Gameplay.Touch0Start.ReadValue<Vector2>();
                RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    _joyStickRect, touchStartPosition, null, out Vector2 localPoint);
                if (localPoint.x < -_joyStickMovementRange ||
                    localPoint.x > _joyStickRect.rect.width + _joyStickMovementRange ||
                    localPoint.y < -_joyStickMovementRange ||
                    localPoint.y > _joyStickRect.rect.height + _joyStickMovementRange)
                {
                    RotateToOrbitPoint(touch0Delta * Time.deltaTime * CAMERA_ROTATION_SPEED_DRAG);
                }
            }

            var touch1Delta = _inputActions.Gameplay.Touch1Delta.ReadValue<float>();
            if (touch1Delta != 0)
            {
                var touchStartPosition = _inputActions.Gameplay.Touch1Start.ReadValue<Vector2>();
                RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    _joyStickRect, touchStartPosition, null, out Vector2 localPoint);
                if (localPoint.x < -_joyStickMovementRange ||
                    localPoint.x > _joyStickRect.rect.width + _joyStickMovementRange ||
                    localPoint.y < -_joyStickMovementRange ||
                    localPoint.y > _joyStickRect.rect.height + _joyStickMovementRange)
                {
                    RotateToOrbitPoint(touch1Delta * Time.deltaTime * CAMERA_ROTATION_SPEED_DRAG);
                }
            }
        }

        private void RotateToOrbitPoint(float yaw)
        {
            var yOffset = new Vector3(0f, _lookAtPointYOffset, 0f);
            _cameraOffset = Quaternion.AngleAxis(yaw, Vector3.up) * _cameraOffset;
            _camera.transform.position = _actualLookAtPoint + _cameraOffset;
            _camera.transform.LookAt(_actualLookAtPoint + yOffset);
        }

        private static float HorizontalToVerticalFov(float horizontalFov, float aspect)
        {
            return Mathf.Rad2Deg * 2 * Mathf.Atan(Mathf.Tan((horizontalFov * Mathf.Deg2Rad) / 2f) / aspect);
        }

        private IEnumerator ShakeAsync(float duration, float amplitude, WaitUntilCanceled waiter = null)
        {
            _cameraAnimationInProgress = true;
            yield return AnimationHelper.ShakeTransformAsync(_camera.transform,
                duration,
                amplitude,
                false,
                true,
                false);
            _cameraAnimationInProgress = false;
            waiter?.CancelWait();
        }

        private IEnumerator MoveAsync(Vector3 position,
            float duration,
            int mode,
            WaitUntilCanceled waiter = null,
            CancellationToken cancellationToken = default)
        {
            _cameraAnimationInProgress = true;
            var curveType = (AnimationCurveType) mode;
            Transform cameraTransform = _camera.transform;
            Vector3 oldPosition = cameraTransform.position;
            yield return AnimationHelper.MoveTransformAsync(cameraTransform, position, duration, curveType, cancellationToken);
            if (!cancellationToken.IsCancellationRequested)
            {
                _lastLookAtPoint += position - oldPosition;
                _cameraOffset = _camera.transform.position - _lastLookAtPoint;
            }
            _cameraAnimationInProgress = false;
            waiter?.CancelWait();
        }

        private IEnumerator OrbitAsync(Quaternion toRotation,
            float duration,
            AnimationCurveType curveType,
            float distanceDelta,
            WaitUntilCanceled waiter = null,
            CancellationToken cancellationToken = default)
        {
            _cameraAnimationInProgress = true;
            Vector3 lookAtPoint = _lastLookAtPoint;
            yield return AnimationHelper.OrbitTransformAroundCenterPointAsync(_camera.transform,
                toRotation,
                lookAtPoint,
                duration,
                curveType,
                distanceDelta,
                cancellationToken);

            if (!cancellationToken.IsCancellationRequested)
            {
                _cameraOffset = _camera.transform.position - lookAtPoint;
            }
            _cameraAnimationInProgress = false;
            waiter?.CancelWait();
        }

        private IEnumerator RotateAsync(Quaternion toRotation,
            float duration,
            AnimationCurveType curveType,
            WaitUntilCanceled waiter = null,
            CancellationToken cancellationToken = default)
        {
            _cameraAnimationInProgress = true;
            yield return AnimationHelper.RotateTransformAsync(_camera.transform,
                toRotation,
                duration,
                curveType,
                cancellationToken);
            _cameraAnimationInProgress = false;
            waiter?.CancelWait();
        }

        public IEnumerator PushAsync(float distance,
            float duration,
            AnimationCurveType curveType,
            WaitUntilCanceled waiter = null,
            CancellationToken cancellationToken = default)
        {
            _cameraAnimationInProgress = true;
            Vector3 oldPosition = _camera.transform.position;
            var oldDistance = Vector3.Distance(oldPosition, _lastLookAtPoint);
            Vector3 cameraFacingDirection = (oldPosition - _lastLookAtPoint).normalized;
            Vector3 newPosition = oldPosition + cameraFacingDirection * (distance - oldDistance);
            Transform cameraTransform = _camera.transform;

            yield return AnimationHelper.MoveTransformAsync(cameraTransform,
                newPosition,
                duration,
                curveType,
                cancellationToken);

            if (!cancellationToken.IsCancellationRequested)
            {
                _cameraOffset = cameraTransform.position - _lastLookAtPoint;
            }
            _cameraAnimationInProgress = false;
            waiter?.CancelWait();
        }

        public IEnumerator FadeAsync(bool fadeIn,
            float duration,
            Color color,
            WaitUntilCanceled waiter = null,
            CancellationToken cancellationToken = default)
        {
            _curtainImage.color = color;

            float from = 1f, to = 0f;
            if (!fadeIn) { from = 0f; to = 1f; }

            yield return AnimationHelper.EnumerateValueAsync(from, to, duration, AnimationCurveType.Linear,
                alpha =>
            {
                _curtainImage.color = new Color(color.r, color.g, color.b, alpha);
            }, cancellationToken);

            if (!cancellationToken.IsCancellationRequested)
            {
                _curtainImage.color = new Color(color.r, color.g, color.b, to);
            }

            waiter?.CancelWait();
        }

        public int GetCurrentAppliedDefaultTransformOption()
        {
            return _currentAppliedDefaultTransformOption;
        }

        private void ApplySceneSettings(ScnSceneInfo sceneInfo)
        {
            switch (sceneInfo.SceneType)
            {
                case ScnSceneType.StoryB:
                    _freeToRotate = false;
                    _lookAtPointYOffset = SCENE_STORY_B_ROOM_FLOOR_HEIGHT;
                    _camera.nearClipPlane = 1f;
                    _camera.farClipPlane = 500f;
                    ApplyDefaultSettings(1);
                    break;
                case ScnSceneType.StoryA:
                    _freeToRotate = true;
                    _lookAtPointYOffset = 0;
                    _camera.nearClipPlane = 2f;
                    _camera.farClipPlane = 800f;
                    ApplyDefaultSettings(0);
                    break;
                case ScnSceneType.Maze:
                    _freeToRotate = true;
                    _lookAtPointYOffset = 0;
                    _camera.nearClipPlane = 2f;
                    _camera.farClipPlane = 800f;
                    ApplyDefaultSettings(0);
                    break;
                default:
                    _lookAtPointYOffset = 0;
                    _camera.nearClipPlane = 1f;
                    _camera.farClipPlane = 500f;
                    ApplyDefaultSettings(0);
                    break;
            }

            _shouldResetVelocity = true;
        }

        private void ApplyDefaultSettings(int option)
        {
            float cameraDistance;
            Quaternion cameraRotation;
            float cameraFov;

            switch (option)
            {
                case 0:
                    cameraFov = HorizontalToVerticalFov(26.0f, 4f/3f);
                    cameraDistance = CAMERA_DEFAULT_DISTANCE;
                    cameraRotation = GameBoxInterpreter.ToUnityRotation(-30.37f, -52.65f, 0f);
                    break;
                case 1:
                    cameraFov = HorizontalToVerticalFov(24.05f, 4f/3f);
                    cameraDistance = CAMERA_SCENE_STORY_B_DISTANCE;
                    cameraRotation = GameBoxInterpreter.ToUnityRotation(-19.48f, 33.24f, 0f);
                    break;
                case 2:
                    cameraFov = HorizontalToVerticalFov(24.05f, 4f/3f);
                    cameraDistance = CAMERA_SCENE_STORY_B_DISTANCE;
                    cameraRotation = GameBoxInterpreter.ToUnityRotation(-19.48f, -33.24f, 0f);
                    break;
                case 3:
                    cameraFov = HorizontalToVerticalFov(24.05f, 4f/3f);
                    cameraDistance = CAMERA_SCENE_STORY_B_DISTANCE;
                    cameraRotation = GameBoxInterpreter.ToUnityRotation(-19.48f, 0f, 0f);
                    break;
                default:
                    return;
            }

            _camera.fieldOfView = cameraFov;

            var yOffset = new Vector3(0f, _lookAtPointYOffset, 0f);

            Vector3 cameraFacingDirection = (cameraRotation * Vector3.forward).normalized;
            Vector3 cameraPosition = _lastLookAtPoint + cameraFacingDirection * -cameraDistance + yOffset;

            Transform cameraTransform = _camera.transform;
            cameraTransform.rotation = cameraRotation;
            cameraTransform.position = cameraPosition;

            _cameraOffset = cameraTransform.position - _lastLookAtPoint;
        }

        public void Execute(CameraSetDefaultTransformCommand command)
        {
            if (!_asyncCameraAnimationCts.IsCancellationRequested) _asyncCameraAnimationCts.Cancel();

            if (_gamePlayController.TryGetPlayerActorLastKnownPosition(out Vector3 playerActorPosition))
            {
                _lastLookAtPoint = playerActorPosition;
            }

            ApplyDefaultSettings(command.Option);
            _currentAppliedDefaultTransformOption = command.Option;
            _free = true;
        }

        public void Execute(CameraSetTransformCommand command)
        {
            if (!_asyncCameraAnimationCts.IsCancellationRequested) _asyncCameraAnimationCts.Cancel();

            _lookAtGameObject = null;

            Vector3 cameraPosition = GameBoxInterpreter.ToUnityPosition(new Vector3(
                command.GameBoxXPosition,
                command.GameBoxYPosition,
                command.GameBoxZPosition));
            Transform cameraTransform = _camera.transform;
            cameraTransform.position = cameraPosition;
            cameraTransform.rotation = GameBoxInterpreter.ToUnityRotation(command.Pitch, command.Yaw, 0f);

            _lastLookAtPoint = cameraTransform.position +
                               cameraTransform.forward * GameBoxInterpreter.ToUnityDistance(command.GameBoxDistance);
            _cameraOffset = cameraPosition - _lastLookAtPoint;

            if (_gameStateManager.GetCurrentState() != GameState.Gameplay)
            {
                _free = false;
            }
        }

        public void Execute(CameraFreeCommand command)
        {
            if (!_asyncCameraAnimationCts.IsCancellationRequested) _asyncCameraAnimationCts.Cancel();

            _lookAtGameObject = null;
            _free = command.Free == 1;
        }

        public void Execute(CameraShakeEffectCommand command)
        {
            if (!_asyncCameraAnimationCts.IsCancellationRequested) _asyncCameraAnimationCts.Cancel();

            var waiter = new WaitUntilCanceled();
            CommandDispatcher<ICommand>.Instance.Dispatch(new ScriptRunnerAddWaiterRequest(waiter));
            StartCoroutine(ShakeAsync(command.Duration, GameBoxInterpreter.ToUnityDistance(command.Amplitude), waiter));
        }

        public void Execute(CameraOrbitCommand command)
        {
            if (!_asyncCameraAnimationCts.IsCancellationRequested) _asyncCameraAnimationCts.Cancel();

            var waiter = new WaitUntilCanceled();
            CommandDispatcher<ICommand>.Instance.Dispatch(new ScriptRunnerAddWaiterRequest(waiter));
            Quaternion rotation = GameBoxInterpreter.ToUnityRotation(command.Pitch, command.Yaw, 0f);
            StartCoroutine(OrbitAsync(rotation, command.Duration, (AnimationCurveType)command.CurveType, 0f, waiter));
        }

        public void Execute(CameraRotateCommand command)
        {
            if (!_asyncCameraAnimationCts.IsCancellationRequested) _asyncCameraAnimationCts.Cancel();

            #if PAL3
            if (true)
            #elif PAL3A
            if (command.Synchronous == 1)
            #endif
            {
                var waiter = new WaitUntilCanceled();
                CommandDispatcher<ICommand>.Instance.Dispatch(new ScriptRunnerAddWaiterRequest(waiter));
                Quaternion rotation = GameBoxInterpreter.ToUnityRotation(command.Pitch, command.Yaw, 0f);
                StartCoroutine(RotateAsync(rotation, command.Duration, (AnimationCurveType)command.CurveType, waiter));
            }
            #if PAL3A
            else
            {
                _asyncCameraAnimationCts = new CancellationTokenSource();
                Quaternion rotation = GameBoxInterpreter.ToUnityRotation(command.Pitch, command.Yaw, 0f);
                StartCoroutine(RotateAsync(rotation,
                    command.Duration,
                    (AnimationCurveType)command.CurveType,
                    waiter: null,
                    _asyncCameraAnimationCts.Token));
            }
            #endif
        }

        #if PAL3A
        public void Execute(CameraOrbitHorizontalCommand command)
        {
            if (!_asyncCameraAnimationCts.IsCancellationRequested) _asyncCameraAnimationCts.Cancel();

            var oldDistance = _cameraOffset.magnitude;
            var newDistance = GameBoxInterpreter.ToUnityDistance(command.GameBoxDistance);
            var distanceDelta = newDistance - oldDistance;

            if (command.Synchronous == 1)
            {
                var waiter = new WaitUntilCanceled();
                CommandDispatcher<ICommand>.Instance.Dispatch(new ScriptRunnerAddWaiterRequest(waiter));
                Quaternion rotation = GameBoxInterpreter.ToUnityRotation(command.Pitch, command.Yaw, 0f);
                StartCoroutine(OrbitAsync(rotation, command.Duration, (AnimationCurveType)command.CurveType, distanceDelta, waiter));
            }
            else
            {
                _asyncCameraAnimationCts = new CancellationTokenSource();
                Quaternion rotation = GameBoxInterpreter.ToUnityRotation(command.Pitch, command.Yaw, 0f);
                StartCoroutine(OrbitAsync(rotation,
                    command.Duration,
                    (AnimationCurveType)command.CurveType,
                    distanceDelta,
                    waiter: null,
                    _asyncCameraAnimationCts.Token));
            }
        }
        #endif

        #if PAL3A
        public void Execute(CameraOrbitVerticalCommand command)
        {
            if (!_asyncCameraAnimationCts.IsCancellationRequested) _asyncCameraAnimationCts.Cancel();

            var oldDistance = _cameraOffset.magnitude;
            var newDistance = GameBoxInterpreter.ToUnityDistance(command.GameBoxDistance);
            var distanceDelta = newDistance - oldDistance;

            if (command.Synchronous == 1)
            {
                var waiter = new WaitUntilCanceled();
                CommandDispatcher<ICommand>.Instance.Dispatch(new ScriptRunnerAddWaiterRequest(waiter));
                Quaternion rotation = GameBoxInterpreter.ToUnityRotation(command.Pitch, command.Yaw, 0f);
                StartCoroutine(OrbitAsync(rotation, command.Duration, (AnimationCurveType)command.CurveType, distanceDelta, waiter));
            }
            else
            {
                _asyncCameraAnimationCts = new CancellationTokenSource();
                Quaternion rotation = GameBoxInterpreter.ToUnityRotation(command.Pitch, command.Yaw, 0f);
                StartCoroutine(OrbitAsync(rotation,
                    command.Duration,
                    (AnimationCurveType)command.CurveType,
                    distanceDelta,
                    waiter: null,
                    _asyncCameraAnimationCts.Token));
            }
        }
        #endif

        public void Execute(CameraFadeInCommand command)
        {
            if (!_cameraFadeAnimationCts.IsCancellationRequested) _cameraFadeAnimationCts.Cancel();
            _cameraFadeAnimationCts = new CancellationTokenSource();
            var waiter = new WaitUntilCanceled();
            CommandDispatcher<ICommand>.Instance.Dispatch(new ScriptRunnerAddWaiterRequest(waiter));
            StartCoroutine(FadeAsync(true, FADE_ANIMATION_DURATION, Color.black, waiter, _cameraFadeAnimationCts.Token));
        }

        public void Execute(CameraFadeInWhiteCommand command)
        {
            if (!_cameraFadeAnimationCts.IsCancellationRequested) _cameraFadeAnimationCts.Cancel();
            _cameraFadeAnimationCts = new CancellationTokenSource();
            var waiter = new WaitUntilCanceled();
            CommandDispatcher<ICommand>.Instance.Dispatch(new ScriptRunnerAddWaiterRequest(waiter));
            StartCoroutine(FadeAsync(true, FADE_ANIMATION_DURATION, Color.white, waiter, _cameraFadeAnimationCts.Token));
        }

        public void Execute(CameraFadeOutCommand command)
        {
            if (!_cameraFadeAnimationCts.IsCancellationRequested) _cameraFadeAnimationCts.Cancel();
            _cameraFadeAnimationCts = new CancellationTokenSource();
            var waiter = new WaitUntilCanceled();
            CommandDispatcher<ICommand>.Instance.Dispatch(new ScriptRunnerAddWaiterRequest(waiter));
            StartCoroutine(FadeAsync(false, FADE_ANIMATION_DURATION, Color.black, waiter, _cameraFadeAnimationCts.Token));
        }

        public void Execute(CameraFadeOutWhiteCommand command)
        {
            if (!_cameraFadeAnimationCts.IsCancellationRequested) _cameraFadeAnimationCts.Cancel();
            _cameraFadeAnimationCts = new CancellationTokenSource();
            var waiter = new WaitUntilCanceled();
            CommandDispatcher<ICommand>.Instance.Dispatch(new ScriptRunnerAddWaiterRequest(waiter));
            StartCoroutine(FadeAsync(false, FADE_ANIMATION_DURATION, Color.white, waiter, _cameraFadeAnimationCts.Token));
        }

        public void Execute(CameraPushCommand command)
        {
            if (!_asyncCameraAnimationCts.IsCancellationRequested) _asyncCameraAnimationCts.Cancel();

            #if PAL3
            if (true)
            #elif PAL3A
            if (command.Synchronous == 1)
            #endif
            {
                var waiter = new WaitUntilCanceled();
                CommandDispatcher<ICommand>.Instance.Dispatch(new ScriptRunnerAddWaiterRequest(waiter));
                var distance = GameBoxInterpreter.ToUnityDistance(command.GameBoxDistance);
                StartCoroutine(PushAsync(distance, command.Duration, (AnimationCurveType)command.CurveType, waiter));
            }
            #if PAL3A
            else
            {
                _asyncCameraAnimationCts = new CancellationTokenSource();
                var distance = GameBoxInterpreter.ToUnityDistance(command.GameBoxDistance);
                StartCoroutine(PushAsync(distance,
                    command.Duration,
                    (AnimationCurveType)command.CurveType,
                    waiter: null,
                    _asyncCameraAnimationCts.Token));
            }
            #endif
        }

        public void Execute(CameraMoveCommand command)
        {
            if (!_asyncCameraAnimationCts.IsCancellationRequested) _asyncCameraAnimationCts.Cancel();

            #if PAL3
            if (true)
            #elif PAL3A
            if (command.Synchronous == 1)
            #endif
            {
                var waiter = new WaitUntilCanceled();
                CommandDispatcher<ICommand>.Instance.Dispatch(new ScriptRunnerAddWaiterRequest(waiter));
                Vector3 position = GameBoxInterpreter.ToUnityPosition(new Vector3(
                    command.GameBoxXPosition,
                    command.GameBoxYPosition,
                    command.GameBoxZPosition));
                StartCoroutine(MoveAsync(position, command.Duration, command.Mode, waiter));
            }
            #if PAL3A
            else
            {
                _asyncCameraAnimationCts = new CancellationTokenSource();
                Vector3 position = GameBoxInterpreter.ToUnityPosition(new Vector3(
                    command.GameBoxXPosition,
                    command.GameBoxYPosition,
                    command.GameBoxZPosition));
                StartCoroutine(MoveAsync(position,
                    command.Duration,
                    command.Mode,
                    waiter: null,
                    _asyncCameraAnimationCts.Token));
            }
            #endif
        }

        public void Execute(CameraSetYawCommand command)
        {
            if (!_asyncCameraAnimationCts.IsCancellationRequested) _asyncCameraAnimationCts.Cancel();
            RotateToOrbitPoint(command.Yaw);
        }

        public void Execute(SceneLeavingCurrentSceneNotification command)
        {
            if (_sceneManager.GetCurrentScene() is not { } currentScene) return;

            // Remember the current scene's camera position and rotation
            if (_free && currentScene.GetSceneInfo().SceneType != ScnSceneType.StoryB)
            {
                var cameraTransform = _camera.transform;
                _cameraLastKnownSceneState.Add((
                    currentScene.GetSceneInfo(),
                    cameraTransform.position,
                    cameraTransform.rotation,
                    _cameraOffset));

                if (_cameraLastKnownSceneState.Count > LAST_KNOWN_SCENE_STATE_LIST_MAX_LENGTH)
                {
                    _cameraLastKnownSceneState.RemoveAt(0);
                }
            }
        }

        public void Execute(ScenePreLoadingNotification notification)
        {
            _currentAppliedDefaultTransformOption = 0;
            ApplySceneSettings(notification.NewSceneInfo);

            // Apply the last known scene state if found in record.
            if (_free && notification.NewSceneInfo.SceneType != ScnSceneType.StoryB)
            {
                if (_cameraLastKnownSceneState.Count > 0 && _cameraLastKnownSceneState.Any(_ =>
                        _.sceneInfo.ModelEquals(notification.NewSceneInfo)))
                {
                    (ScnSceneInfo _, Vector3 cameraPosition, Quaternion cameraRotation, Vector3 cameraOffset) =
                        _cameraLastKnownSceneState.Last(_ => _.sceneInfo.ModelEquals(notification.NewSceneInfo));

                    _camera.transform.SetPositionAndRotation(cameraPosition, cameraRotation);
                    _cameraOffset = cameraOffset;
                }
            }
        }

        public void Execute(CameraFocusOnActorCommand command)
        {
            if (command.ActorId == ActorConstants.PlayerActorVirtualID) return;
            if (!_asyncCameraAnimationCts.IsCancellationRequested) _asyncCameraAnimationCts.Cancel();
            _lookAtGameObject = _sceneManager.GetCurrentScene().GetActorGameObject(command.ActorId);
        }

        public void Execute(CameraFocusOnSceneObjectCommand command)
        {
            if (!_asyncCameraAnimationCts.IsCancellationRequested) _asyncCameraAnimationCts.Cancel();

            _lookAtGameObject = _sceneManager.GetCurrentScene()
                .GetSceneObject(command.SceneObjectId).GetGameObject();
        }

        public void Execute(GameStateChangedNotification command)
        {
            if (command.NewState == GameState.Gameplay) _free = true;
        }

        public void Execute(ResetGameStateCommand command)
        {
            _cameraLastKnownSceneState.Clear();
            _free = true;
        }
    }
}