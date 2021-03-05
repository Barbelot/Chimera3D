using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.VFX;

namespace Chimera3D
{
    public class FluidEmitter3D : MonoBehaviour
    {
		[Header("Simulation")]
		public string simulationID = "Main";

		[Header("Emission")]
		public bool emitFluid = true;
		public float radius = 0.05f;
		public float densityAmount = 1;
		public float temperatureAmount = 10;

		[Header("VFX")]
		public VisualEffect vfx;

		[Header("Gizmos")]
		public bool showGizmos = true;

		private Vector3 previousPosition = Vector3.zero;

		private SmokeFluidSim _simulation;

		private bool previousPositionReady = false;

		private void OnEnable() {

			previousPositionReady = false;

			if (!vfx)
				vfx = GetComponentInChildren<VisualEffect>();

			AddEmitter();
			UpdateEmitter();
			UpdateVFX();
		}

		private void Update() {

			UpdateVFX();
		}

		private void LateUpdate() {

			UpdateEmitter();
		}

		private void OnDisable() {

			RemoveEmitter();
		}

		private void OnDrawGizmos() {

			if (!showGizmos)
				return;

			Gizmos.color = Color.yellow;

			Gizmos.DrawWireSphere(transform.position, radius);
		}

		void AddEmitter() {

			foreach (var sim in FindObjectsOfType<SmokeFluidSim>()) {
				if (sim.simulationID == simulationID) {
					_simulation = sim; break;
				}
			}

			if (!_simulation)
				return;

			if (emitFluid)
				_simulation.AddEmitter(this);
			else
				_simulation.Initialize();

			if (vfx) {
				vfx.SetTexture("VelocityField", _simulation.m_velocityField);
				vfx.SetVector3("FieldSize", _simulation.m_size);
			}
		}

		void UpdateEmitter() {

			previousPosition = transform.position;
			previousPositionReady = true;
		}

		void RemoveEmitter() {

			if (_simulation)
				_simulation.RemoveEmitter(this);
		}

		void UpdateVFX() {

			if (!vfx)
				return;

			vfx.SetVector3("EmissionPosition_position", transform.position);
			vfx.SetVector3("EmissionPreviousPosition_position", previousPositionReady ? previousPosition : transform.position);
			vfx.SetFloat("EmissionRadius", radius);
		}
	}
}
