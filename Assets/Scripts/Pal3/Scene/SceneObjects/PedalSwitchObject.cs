﻿// ---------------------------------------------------------------------------------------------
//  Copyright (c) 2021-2023, Jiaqi Liu. All rights reserved.
//  See LICENSE file in the project root for license information.
// ---------------------------------------------------------------------------------------------

namespace Pal3.Scene.SceneObjects
{
    using System;
    using System.Collections;
    using Actor;
    using Common;
    using Core.Animation;
    using Core.DataReader.Scn;
    using Core.Services;
    using Data;
    using State;
    using UnityEngine;
    using Object = UnityEngine.Object;

    [ScnSceneObject(ScnSceneObjectType.PedalSwitch)]
    public sealed class PedalSwitchObject : SceneObject
    {
        private const float DESCENDING_HEIGHT = 0.25f;
        private const float DESCENDING_ANIMATION_DURATION = 2f;

        private StandingPlatformController _platformController;

        private readonly GameStateManager _gameStateManager;

        private bool _isInteractionInProgress;

        public PedalSwitchObject(ScnObjectInfo objectInfo, ScnSceneInfo sceneInfo)
            : base(objectInfo, sceneInfo)
        {
            _gameStateManager = ServiceLocator.Instance.Get<GameStateManager>();
        }

        public override GameObject Activate(GameResourceProvider resourceProvider, Color tintColor)
        {
            if (Activated) return GetGameObject();

            GameObject sceneGameObject = base.Activate(resourceProvider, tintColor);

            Bounds bounds = GetPolyModelRenderer().GetMeshBounds();

            // _h1.pol
            if (ObjectInfo.Name.Equals("_h1.pol", StringComparison.OrdinalIgnoreCase))
            {
                bounds = new Bounds
                {
                    center = new Vector3(0f, -0.2f, 0f),
                    size = new Vector3(3f, 0.5f, 3f),
                };
            }
            else if (ObjectInfo.Name.Equals("_c.pol", StringComparison.OrdinalIgnoreCase))
            {
                bounds = new Bounds
                {
                    center = new Vector3(0f, -0.2f, -0.5f),
                    size = new Vector3(3.5f, 0.5f, 6f),
                };
            }

            _platformController = sceneGameObject.AddComponent<StandingPlatformController>();
            _platformController.SetBounds(bounds, ObjectInfo.LayerIndex);
            _platformController.OnPlayerActorEntered += OnPlayerActorEntered;

            // Set to final position if it is already activated
            if (ObjectInfo.Times == 0)
            {
                Vector3 finalPosition = sceneGameObject.transform.position;
                finalPosition.y -= DESCENDING_HEIGHT;
                sceneGameObject.transform.position = finalPosition;
            }

            return sceneGameObject;
        }

        private void OnPlayerActorEntered(object sender, GameObject playerActorGameObject)
        {
            // Prevent duplicate triggers
            if (_gameStateManager.GetCurrentState() != GameState.Gameplay) return;

            if (!IsInteractableBasedOnTimesCount()) return;

            if (_isInteractionInProgress) return;
            _isInteractionInProgress = true;

            RequestForInteraction();
        }

        public override IEnumerator InteractAsync(InteractionContext ctx)
        {
            GameObject pedalSwitchGo = GetGameObject();
            var platformController = pedalSwitchGo.GetComponent<StandingPlatformController>();
            Vector3 platformCenterPosition = platformController.GetCollider().bounds.center;
            var actorStandingPosition = new Vector3(
                platformCenterPosition.x,
                platformController.GetPlatformHeight(),
                platformCenterPosition.z);

            var actorMovementController = ctx.PlayerActorGameObject.GetComponent<ActorMovementController>();

            yield return actorMovementController.MoveDirectlyToAsync(actorStandingPosition, 0, true);

            // Play descending animation
            Vector3 finalPosition = pedalSwitchGo.transform.position;
            finalPosition.y -= DESCENDING_HEIGHT;

            yield return AnimationHelper.MoveTransformAsync(pedalSwitchGo.transform,
                finalPosition,
                DESCENDING_ANIMATION_DURATION,
                AnimationCurveType.Sine);

            yield return ExecuteScriptAndWaitForFinishIfAnyAsync();

            yield return ActivateOrInteractWithLinkedObjectIfAnyAsync(ctx);

            _isInteractionInProgress = false;
        }

        public override void Deactivate()
        {
            if (_platformController != null)
            {
                _platformController.OnPlayerActorEntered -= OnPlayerActorEntered;
                Object.Destroy(_platformController);
            }

            base.Deactivate();
        }
    }
}