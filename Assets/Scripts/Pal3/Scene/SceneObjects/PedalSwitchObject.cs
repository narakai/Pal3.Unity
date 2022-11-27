﻿// ---------------------------------------------------------------------------------------------
//  Copyright (c) 2021-2022, Jiaqi Liu. All rights reserved.
//  See LICENSE file in the project root for license information.
// ---------------------------------------------------------------------------------------------

namespace Pal3.Scene.SceneObjects
{
    using System.Collections;
    using Actor;
    using Command;
    using Command.SceCommands;
    using Common;
    using Core.Animation;
    using Core.DataReader.Scn;
    using Core.Services;
    using Data;
    using Player;
    using UnityEngine;

    [ScnSceneObject(ScnSceneObjectType.PedalSwitch)]
    public class PedalSwitchObject : SceneObject
    {
        public const float DescendingHeight = 0.25f;
        public const float DescendingAnimationDuration = 2f;
        
        private StandingPlatformController _platformController;
        private PedalSwitchObjectController _pedalSwitchObjectController;
        
        private readonly PlayerManager _playerManager;

        public PedalSwitchObject(ScnObjectInfo objectInfo, ScnSceneInfo sceneInfo)
            : base(objectInfo, sceneInfo)
        {
            _playerManager = ServiceLocator.Instance.Get<PlayerManager>();
        }
        
        public override GameObject Activate(GameResourceProvider resourceProvider, Color tintColor)
        {
            if (Activated) return GetGameObject();
            
            GameObject sceneGameObject = base.Activate(resourceProvider, tintColor);
            
            var bounds = new Bounds
            {
                center = new Vector3(0f, -0.2f, 0f),
                size = new Vector3(3f, 0.5f, 3f),
            };
            
            _platformController = sceneGameObject.AddComponent<StandingPlatformController>();
            _platformController.SetBounds(bounds, ObjectInfo.LayerIndex);
            _platformController.OnTriggerEntered += OnPlatformTriggerEntered;
            
            _pedalSwitchObjectController = sceneGameObject.AddComponent<PedalSwitchObjectController>();
            _pedalSwitchObjectController.Init(this);

            // Set to final position if the gravity trigger is already activated
            if (ObjectInfo.Times == 0)
            {
                Vector3 finalPosition = sceneGameObject.transform.position;
                finalPosition.y -= DescendingHeight;
                sceneGameObject.transform.position = finalPosition;
            }
            
            return sceneGameObject;
        }

        private void OnPlatformTriggerEntered(object sender, Collider collider)
        {
            if (!IsInteractableBasedOnTimesCount()) return;
            
            // Check if the player actor is on the platform
            if (collider.gameObject.GetComponent<ActorController>() is {} actorController &&
                actorController.GetActor().Info.Id == (byte)_playerManager.GetPlayerActor())
            {
                _pedalSwitchObjectController.Interact(collider.gameObject);
            }
        }

        public override void Deactivate()
        {
            if (_platformController != null)
            {
                _platformController.OnTriggerEntered -= OnPlatformTriggerEntered;
                Object.Destroy(_platformController);
            }
            
            if (_pedalSwitchObjectController != null)
            {
                Object.Destroy(_pedalSwitchObjectController);
            }
            
            base.Deactivate();
        }
    }
    
    internal class PedalSwitchObjectController : MonoBehaviour
    {
        private PedalSwitchObject _pedalSwitchObject;
        
        public void Init(PedalSwitchObject pedalSwitchObject)
        {
            _pedalSwitchObject = pedalSwitchObject;
        }
        
        public void Interact(GameObject playerActorGameObject)
        {
            CommandDispatcher<ICommand>.Instance.Dispatch(new PlayerEnableInputCommand(0));
            StartCoroutine(InteractInternal(playerActorGameObject));
        }
        
        private IEnumerator InteractInternal(GameObject playerActorGameObject)
        {
            GameObject pedalSwitchGo = _pedalSwitchObject.GetGameObject();
            var platformController = pedalSwitchGo.GetComponent<StandingPlatformController>();
            Vector3 platformPosition = platformController.transform.position;
            var actorStandingPosition = new Vector3(
                platformPosition.x,
                platformController.GetPlatformHeight(),
                platformPosition.z);
            
            var movementController = playerActorGameObject.GetComponent<ActorMovementController>();
            yield return movementController.MoveDirectlyTo(actorStandingPosition, 0);
            
            // Play descending animation
            Vector3 finalPosition = transform.position;
            finalPosition.y -= PedalSwitchObject.DescendingHeight;
            
            yield return AnimationHelper.MoveTransform(pedalSwitchGo.transform,
                finalPosition,
                PedalSwitchObject.DescendingAnimationDuration,
                AnimationCurveType.Sine);

            _pedalSwitchObject.ExecuteScriptIfAny();

            CommandDispatcher<ICommand>.Instance.Dispatch(new PlayerEnableInputCommand(1));
        }
    }
}