using UnityEngine;
using System.Collections.Generic;
using UnityEngine.VFX;

namespace Chimera3D
{
	public class SmokeFluidSim : MonoBehaviour
	{
		//DONT CHANGE THESE
		const int READ = 0;
		const int WRITE = 1;
		const int PHI_N_HAT = 0;
		const int PHI_N_1_HAT = 1;

		public enum ADVECTION { NORMAL = 1, BFECC = 2, MACCORMACK = 3 };

		//You can change this but you must change the same value in all the compute shader's to the same
		//Must be a pow2 number
		const int NUM_THREADS = 8;

		[Header("ID")]
		public string simulationID = "Main";

		[Header("Simulation")]
		//You can change this or even use Time.DeltaTime but large time steps can cause numerical errors
		public float m_timeStep = 6.0f;
		public ADVECTION m_advectionType = ADVECTION.NORMAL;
		public Vector3Int m_resolution = new Vector3Int(128, 128, 128);
		public Vector3 m_size = Vector3.one;
		public int m_iterations = 10;
		public float m_vorticityStrength = 1.0f;
		//public float m_densityAmount = 1.0f;
		[Range(0, 1)] public float m_densityDissipation = 0.999f;
		public float m_densityBuoyancy = 1.0f;
		[Range(0, 1)] public float m_densityBuoyancySphericality = 0.0f;
		public Vector3 m_densityBuoyancyDirection = Vector3.up;
		public float m_densityWeight = 0.0125f;
		//public float m_temperatureAmount = 10.0f;
		[Range(0, 1)] public float m_temperatureDissipation = 0.995f;
		[Range(0, 1)] public float m_velocityDissipation = 0.995f;
		//public float m_inputRadius = 0.04f;
		//public Vector4 m_inputPos = new Vector4(0.5f, 0.1f, 0.5f, 0.0f);

		float m_ambientTemperature = 0.0f;

		[Header("Raycast Renderer")]
		public Renderer raycastRenderer;

		[Header("VFX")]
		public VisualEffect vfx;

		[Header("Compute Shaders")]
		public ComputeShader m_applyImpulse;
		public ComputeShader m_applyAdvect;
		public ComputeShader m_computeVorticity;
		public ComputeShader m_computeDivergence;
		public ComputeShader m_computeJacobi;
		public ComputeShader m_computeProjection;
		public ComputeShader m_computeConfinement;
		public ComputeShader m_computeObstacles;
		public ComputeShader m_applyBuoyancy;
		public ComputeShader m_computeVelocityField;

		[Header("Debug")]
		public Material texture3DSliceMaterial;
		public RenderTexture m_velocityField;
		public float debugFloat;

		Vector4 m_cachedResolution;
		ComputeBuffer[] m_density, m_velocity, m_pressure, m_temperature, m_phi;
		ComputeBuffer m_temp3f, m_obstacles;

		private List<FluidEmitter3D> m_emitters;

		private bool m_initialized = false;

		#region MonoBehaviour Implementation

		private void OnEnable() {

			if (!m_initialized)
				Initialize();
		}

		void Start() {
			Application.targetFrameRate = 60;
		}

		void Update() {

			if (!m_initialized)
				Initialize();

			float dt = Time.deltaTime * m_timeStep;

			//First off advect any buffers that contain physical quantities like density or temperature by the 
			//velocity field. Advection is what moves values around.
			ApplyAdvection(dt, m_temperatureDissipation, 0.0f, m_temperature);

			//Normal advection can cause smoothing of the advected field making the results look less interesting.
			//BFECC is a method of advection that helps to prevents this smoothing at a extra performance cost but is less numerically stable.
			//MacCormack does the same as BFECC but is more (not completely) numerically stable and is more costly
			if (m_advectionType == ADVECTION.BFECC) {
				ApplyAdvection(dt, 1.0f, 0.0f, m_density, 1.0f); //advect forward into write buffer
				ApplyAdvection(dt, 1.0f, 0.0f, m_density[READ], m_phi[PHI_N_HAT], -1.0f); //advect back into phi_n_hat buffer
				ApplyAdvectionBFECC(dt, m_densityDissipation, 0.0f, m_density); //advect using BFECC
			} else if (m_advectionType == ADVECTION.MACCORMACK) {
				ApplyAdvection(dt, 1.0f, 0.0f, m_density[READ], m_phi[PHI_N_1_HAT], 1.0f); //advect forward into phi_n_1_hat buffer
				ApplyAdvection(dt, 1.0f, 0.0f, m_phi[PHI_N_1_HAT], m_phi[PHI_N_HAT], -1.0f); //advect back into phi_n_hat buffer
				ApplyAdvectionMacCormack(dt, m_densityDissipation, 0.0f, m_density);
			} else {
				ApplyAdvection(dt, m_densityDissipation, 0.0f, m_density);
			}

			//The velocity field also advects its self. 
			ApplyAdvectionVelocity(dt);

			//Apply the effect the sinking colder smoke has on the velocity field
			ApplyBuoyancy(dt);

			for (int i = 0; i < m_emitters.Count; i++) {
				//Adds a certain amount of density (the visible smoke) and temperate
				ApplyImpulse(dt, m_emitters[i].radius, m_emitters[i].densityAmount, GetBufferPosition(m_emitters[i].transform.position), m_density);
				ApplyImpulse(dt, m_emitters[i].radius, m_emitters[i].temperatureAmount, GetBufferPosition(m_emitters[i].transform.position), m_temperature);
			}

			//The fuild sim math tends to remove the swirling movement of fluids.
			//This step will try and add it back in
			ComputeVorticityConfinement(dt);

			//Compute the divergence of the velocity field. In fluid simulation the
			//fluid is modelled as being incompressible meaning that the volume of the fluid
			//does not change over time. The divergence is the amount the field has deviated from being divergence free
			ComputeDivergence();

			//This computes the pressure need return the fluid to a divergence free condition
			ComputePressure();

			//Subtract the pressure field from the velocity field enforcing the divergence free conditions
			ComputeProjection();

			//Update velocity field texture
			ComputeVelocityField();

			//rotation of box not support because ray cast in shader uses a AABB intersection
			transform.rotation = Quaternion.identity;

			//Bind raycast material
			if (raycastRenderer) {
				raycastRenderer.material.SetVector("_Translate", raycastRenderer.transform.position);
				raycastRenderer.material.SetVector("_Scale", m_size);
				raycastRenderer.transform.localScale = m_size;
				raycastRenderer.material.SetBuffer("_Density", m_density[READ]);
				raycastRenderer.material.SetBuffer("_Velocity", m_velocity[READ]);
				raycastRenderer.material.SetVector("_Resolution", m_cachedResolution);
			}

		}

		void OnDisable() {

			if(m_initialized)
				CleanUp();
		}

		#endregion

		public void Initialize() {

			if (m_initialized)
				return;

			//Dimension sizes must be pow2 numbers
			m_resolution = new Vector3Int(Mathf.ClosestPowerOfTwo(m_resolution.x), Mathf.ClosestPowerOfTwo(m_resolution.y), Mathf.ClosestPowerOfTwo(m_resolution.z));

			//Put all dimension sizes in a vector for easy parsing to shader and also prevents user changing
			//dimension sizes during play
			m_cachedResolution = new Vector4(m_resolution.x, m_resolution.y, m_resolution.z, 0);

			//Create all the buffers needed
			int SIZE = m_resolution.x * m_resolution.y * m_resolution.z;

			m_density = new ComputeBuffer[2];
			m_density[READ] = new ComputeBuffer(SIZE, sizeof(float));
			m_density[WRITE] = new ComputeBuffer(SIZE, sizeof(float));

			m_temperature = new ComputeBuffer[2];
			m_temperature[READ] = new ComputeBuffer(SIZE, sizeof(float));
			m_temperature[WRITE] = new ComputeBuffer(SIZE, sizeof(float));

			m_phi = new ComputeBuffer[2];
			m_phi[READ] = new ComputeBuffer(SIZE, sizeof(float));
			m_phi[WRITE] = new ComputeBuffer(SIZE, sizeof(float));

			m_velocity = new ComputeBuffer[2];
			m_velocity[READ] = new ComputeBuffer(SIZE, sizeof(float) * 3);
			m_velocity[WRITE] = new ComputeBuffer(SIZE, sizeof(float) * 3);

			m_pressure = new ComputeBuffer[2];
			m_pressure[READ] = new ComputeBuffer(SIZE, sizeof(float));
			m_pressure[WRITE] = new ComputeBuffer(SIZE, sizeof(float));

			m_obstacles = new ComputeBuffer(SIZE, sizeof(float));

			m_temp3f = new ComputeBuffer(SIZE, sizeof(float) * 3);

			m_velocityField = new RenderTexture(m_resolution.x, m_resolution.y, 0, RenderTextureFormat.ARGBFloat);
			m_velocityField.enableRandomWrite = true;
			m_velocityField.dimension = UnityEngine.Rendering.TextureDimension.Tex3D;
			m_velocityField.volumeDepth = m_resolution.z;
			m_velocityField.Create();

			//Any areas that are obstacles need to be masked of in the obstacle buffer
			//At the moment is only the border around the edge of the buffers to enforce non-slip boundary conditions
			ComputeObstacles();

			//Bind VFX
			if (vfx) {
				vfx.SetTexture("VelocityField", m_velocityField);
				vfx.SetVector3("FieldSize", m_size);
			}

			//Bind Debug
			if(texture3DSliceMaterial)
				texture3DSliceMaterial.SetTexture("InputTexture3D", m_velocityField);

			//Initialize emitters
			InitializeEmitters();

			m_initialized = true;
		}

		void CleanUp() {

			m_density[READ].Release();
			m_density[WRITE].Release();
			m_temperature[READ].Release();
			m_temperature[WRITE].Release();
			m_phi[PHI_N_1_HAT].Release();
			m_phi[PHI_N_HAT].Release();
			m_velocity[READ].Release();
			m_velocity[WRITE].Release();
			m_pressure[READ].Release();
			m_pressure[WRITE].Release();
			m_obstacles.Release();
			m_temp3f.Release();
			m_velocityField.Release();

			m_initialized = false;
		}

		void Swap(ComputeBuffer[] buffer) {
			ComputeBuffer tmp = buffer[READ];
			buffer[READ] = buffer[WRITE];
			buffer[WRITE] = tmp;
		}

		Vector3 GetBufferPosition(Vector3 worldPos) {

			return new Vector3(worldPos.x / m_size.x, worldPos.y / m_size.y, worldPos.z / m_size.z) + Vector3.one * 0.5f;
		}

		#region Kernel Functions

		void ComputeObstacles() {
			m_computeObstacles.SetVector("_Resolution", m_cachedResolution);
			m_computeObstacles.SetBuffer(0, "_Write", m_obstacles);
			m_computeObstacles.Dispatch(0, (int)m_cachedResolution.x / NUM_THREADS, (int)m_cachedResolution.y / NUM_THREADS, (int)m_cachedResolution.z / NUM_THREADS);

		}

		void ApplyImpulse(float dt, float radius, float amount, Vector4 pos, ComputeBuffer[] buffer) {
			m_applyImpulse.SetVector("_Resolution", m_cachedResolution);
			m_applyImpulse.SetFloat("_Radius", radius);
			m_applyImpulse.SetFloat("_Amount", amount);
			m_applyImpulse.SetFloat("_DeltaTime", dt);
			m_applyImpulse.SetVector("_Pos", pos);

			m_applyImpulse.SetBuffer(0, "_Read", buffer[READ]);
			m_applyImpulse.SetBuffer(0, "_Write", buffer[WRITE]);

			m_applyImpulse.Dispatch(0, (int)m_cachedResolution.x / NUM_THREADS, (int)m_cachedResolution.y / NUM_THREADS, (int)m_cachedResolution.z / NUM_THREADS);

			Swap(buffer);
		}

		void ApplyBuoyancy(float dt) {
			m_applyBuoyancy.SetVector("_Resolution", m_cachedResolution);
			m_applyBuoyancy.SetVector("_Up", new Vector4(m_densityBuoyancyDirection.x, m_densityBuoyancyDirection.y, m_densityBuoyancyDirection.z, m_densityBuoyancySphericality));
			m_applyBuoyancy.SetFloat("_Buoyancy", m_densityBuoyancy);
			m_applyBuoyancy.SetFloat("_AmbientTemperature", m_ambientTemperature);
			m_applyBuoyancy.SetFloat("_Weight", m_densityWeight);
			m_applyBuoyancy.SetFloat("_DeltaTime", dt);

			m_applyBuoyancy.SetBuffer(0, "_Write", m_velocity[WRITE]);
			m_applyBuoyancy.SetBuffer(0, "_Velocity", m_velocity[READ]);
			m_applyBuoyancy.SetBuffer(0, "_Density", m_density[READ]);
			m_applyBuoyancy.SetBuffer(0, "_Temperature", m_temperature[READ]);

			m_applyBuoyancy.Dispatch(0, (int)m_cachedResolution.x / NUM_THREADS, (int)m_cachedResolution.y / NUM_THREADS, (int)m_cachedResolution.z / NUM_THREADS);

			Swap(m_velocity);
		}

		void ApplyAdvection(float dt, float dissipation, float decay, ComputeBuffer[] buffer, float forward = 1.0f) {
			m_applyAdvect.SetVector("_Resolution", m_cachedResolution);
			m_applyAdvect.SetFloat("_DeltaTime", dt);
			m_applyAdvect.SetFloat("_Dissipate", dissipation);
			m_applyAdvect.SetFloat("_Forward", forward);
			m_applyAdvect.SetFloat("_Decay", decay);

			m_applyAdvect.SetBuffer((int)ADVECTION.NORMAL, "_Read1f", buffer[READ]);
			m_applyAdvect.SetBuffer((int)ADVECTION.NORMAL, "_Write1f", buffer[WRITE]);
			m_applyAdvect.SetBuffer((int)ADVECTION.NORMAL, "_Velocity", m_velocity[READ]);
			m_applyAdvect.SetBuffer((int)ADVECTION.NORMAL, "_Obstacles", m_obstacles);

			m_applyAdvect.Dispatch((int)ADVECTION.NORMAL, (int)m_cachedResolution.x / NUM_THREADS, (int)m_cachedResolution.y / NUM_THREADS, (int)m_cachedResolution.z / NUM_THREADS);

			Swap(buffer);
		}

		void ApplyAdvection(float dt, float dissipation, float decay, ComputeBuffer read, ComputeBuffer write, float forward = 1.0f) {
			m_applyAdvect.SetVector("_Resolution", m_cachedResolution);
			m_applyAdvect.SetFloat("_DeltaTime", dt);
			m_applyAdvect.SetFloat("_Dissipate", dissipation);
			m_applyAdvect.SetFloat("_Forward", forward);
			m_applyAdvect.SetFloat("_Decay", decay);

			m_applyAdvect.SetBuffer((int)ADVECTION.NORMAL, "_Read1f", read);
			m_applyAdvect.SetBuffer((int)ADVECTION.NORMAL, "_Write1f", write);
			m_applyAdvect.SetBuffer((int)ADVECTION.NORMAL, "_Velocity", m_velocity[READ]);
			m_applyAdvect.SetBuffer((int)ADVECTION.NORMAL, "_Obstacles", m_obstacles);

			m_applyAdvect.Dispatch((int)ADVECTION.NORMAL, (int)m_cachedResolution.x / NUM_THREADS, (int)m_cachedResolution.y / NUM_THREADS, (int)m_cachedResolution.z / NUM_THREADS);
		}

		void ApplyAdvectionBFECC(float dt, float dissipation, float decay, ComputeBuffer[] buffer) {
			m_applyAdvect.SetVector("_Resolution", m_cachedResolution);
			m_applyAdvect.SetFloat("_DeltaTime", dt);
			m_applyAdvect.SetFloat("_Dissipate", dissipation);
			m_applyAdvect.SetFloat("_Forward", 1.0f);
			m_applyAdvect.SetFloat("_Decay", decay);

			m_applyAdvect.SetBuffer((int)ADVECTION.BFECC, "_Read1f", buffer[READ]);
			m_applyAdvect.SetBuffer((int)ADVECTION.BFECC, "_Write1f", buffer[WRITE]);
			m_applyAdvect.SetBuffer((int)ADVECTION.BFECC, "_Phi_n_hat", m_phi[PHI_N_HAT]);
			m_applyAdvect.SetBuffer((int)ADVECTION.BFECC, "_Velocity", m_velocity[READ]);
			m_applyAdvect.SetBuffer((int)ADVECTION.BFECC, "_Obstacles", m_obstacles);

			m_applyAdvect.Dispatch((int)ADVECTION.BFECC, (int)m_cachedResolution.x / NUM_THREADS, (int)m_cachedResolution.y / NUM_THREADS, (int)m_cachedResolution.z / NUM_THREADS);

			Swap(buffer);
		}

		void ApplyAdvectionMacCormack(float dt, float dissipation, float decay, ComputeBuffer[] buffer) {
			m_applyAdvect.SetVector("_Resolution", m_cachedResolution);
			m_applyAdvect.SetFloat("_DeltaTime", dt);
			m_applyAdvect.SetFloat("_Dissipate", dissipation);
			m_applyAdvect.SetFloat("_Forward", 1.0f);
			m_applyAdvect.SetFloat("_Decay", decay);

			m_applyAdvect.SetBuffer((int)ADVECTION.MACCORMACK, "_Read1f", buffer[READ]);
			m_applyAdvect.SetBuffer((int)ADVECTION.MACCORMACK, "_Write1f", buffer[WRITE]);
			m_applyAdvect.SetBuffer((int)ADVECTION.MACCORMACK, "_Phi_n_1_hat", m_phi[PHI_N_1_HAT]);
			m_applyAdvect.SetBuffer((int)ADVECTION.MACCORMACK, "_Phi_n_hat", m_phi[PHI_N_HAT]);
			m_applyAdvect.SetBuffer((int)ADVECTION.MACCORMACK, "_Velocity", m_velocity[READ]);
			m_applyAdvect.SetBuffer((int)ADVECTION.MACCORMACK, "_Obstacles", m_obstacles);

			m_applyAdvect.Dispatch((int)ADVECTION.MACCORMACK, (int)m_cachedResolution.x / NUM_THREADS, (int)m_cachedResolution.y / NUM_THREADS, (int)m_cachedResolution.z / NUM_THREADS);

			Swap(buffer);
		}

		void ApplyAdvectionVelocity(float dt) {
			m_applyAdvect.SetVector("_Resolution", m_cachedResolution);
			m_applyAdvect.SetFloat("_DeltaTime", dt);
			m_applyAdvect.SetFloat("_Dissipate", m_velocityDissipation);
			m_applyAdvect.SetFloat("_Forward", 1.0f);
			m_applyAdvect.SetFloat("_Decay", 0.0f);

			m_applyAdvect.SetBuffer(0, "_Read3f", m_velocity[READ]);
			m_applyAdvect.SetBuffer(0, "_Write3f", m_velocity[WRITE]);
			m_applyAdvect.SetBuffer(0, "_Velocity", m_velocity[READ]);
			m_applyAdvect.SetBuffer(0, "_Obstacles", m_obstacles);

			m_applyAdvect.Dispatch(0, (int)m_cachedResolution.x / NUM_THREADS, (int)m_cachedResolution.y / NUM_THREADS, (int)m_cachedResolution.z / NUM_THREADS);

			Swap(m_velocity);
		}

		void ComputeVorticityConfinement(float dt) {
			m_computeVorticity.SetVector("_Resolution", m_cachedResolution);

			m_computeVorticity.SetBuffer(0, "_Write", m_temp3f);
			m_computeVorticity.SetBuffer(0, "_Velocity", m_velocity[READ]);

			m_computeVorticity.Dispatch(0, (int)m_cachedResolution.x / NUM_THREADS, (int)m_cachedResolution.y / NUM_THREADS, (int)m_cachedResolution.z / NUM_THREADS);

			m_computeConfinement.SetVector("_Resolution", m_cachedResolution);
			m_computeConfinement.SetFloat("_DeltaTime", dt);
			m_computeConfinement.SetFloat("_Epsilon", m_vorticityStrength);

			m_computeConfinement.SetBuffer(0, "_Write", m_velocity[WRITE]);
			m_computeConfinement.SetBuffer(0, "_Read", m_velocity[READ]);
			m_computeConfinement.SetBuffer(0, "_Vorticity", m_temp3f);

			m_computeConfinement.Dispatch(0, (int)m_cachedResolution.x / NUM_THREADS, (int)m_cachedResolution.y / NUM_THREADS, (int)m_cachedResolution.z / NUM_THREADS);

			Swap(m_velocity);
		}

		void ComputeDivergence() {
			m_computeDivergence.SetVector("_Resolution", m_cachedResolution);

			m_computeDivergence.SetBuffer(0, "_Write", m_temp3f);
			m_computeDivergence.SetBuffer(0, "_Velocity", m_velocity[READ]);
			m_computeDivergence.SetBuffer(0, "_Obstacles", m_obstacles);

			m_computeDivergence.Dispatch(0, (int)m_cachedResolution.x / NUM_THREADS, (int)m_cachedResolution.y / NUM_THREADS, (int)m_cachedResolution.z / NUM_THREADS);
		}

		void ComputePressure() {
			m_computeJacobi.SetVector("_Resolution", m_cachedResolution);
			m_computeJacobi.SetBuffer(0, "_Divergence", m_temp3f);
			m_computeJacobi.SetBuffer(0, "_Obstacles", m_obstacles);

			for (int i = 0; i < m_iterations; i++) {
				m_computeJacobi.SetBuffer(0, "_Write", m_pressure[WRITE]);
				m_computeJacobi.SetBuffer(0, "_Pressure", m_pressure[READ]);

				m_computeJacobi.Dispatch(0, (int)m_cachedResolution.x / NUM_THREADS, (int)m_cachedResolution.y / NUM_THREADS, (int)m_cachedResolution.z / NUM_THREADS);

				Swap(m_pressure);
			}
		}

		void ComputeProjection() {
			m_computeProjection.SetVector("_Resolution", m_cachedResolution);
			m_computeProjection.SetBuffer(0, "_Obstacles", m_obstacles);

			m_computeProjection.SetBuffer(0, "_Pressure", m_pressure[READ]);
			m_computeProjection.SetBuffer(0, "_Velocity", m_velocity[READ]);
			m_computeProjection.SetBuffer(0, "_Write", m_velocity[WRITE]);

			m_computeProjection.Dispatch(0, (int)m_cachedResolution.x / NUM_THREADS, (int)m_cachedResolution.y / NUM_THREADS, (int)m_cachedResolution.z / NUM_THREADS);

			Swap(m_velocity);
		}

		void ComputeVelocityField() {
			m_computeVelocityField.SetVector("_Resolution", m_cachedResolution);

			m_computeVelocityField.SetBuffer(0, "_Velocity", m_velocity[READ]);
			m_computeVelocityField.SetBuffer(0, "_Density", m_density[READ]);
			m_computeVelocityField.SetTexture(0, "_VelocityField", m_velocityField);

			m_computeVelocityField.Dispatch(0, (int)m_cachedResolution.x / NUM_THREADS, (int)m_cachedResolution.y / NUM_THREADS, (int)m_cachedResolution.z / NUM_THREADS);
		}

		#endregion

		#region Emitters

		void InitializeEmitters() {

			CreateEmittersList();
		}

		void CreateEmittersList() {

			m_emitters = new List<FluidEmitter3D>();
		}

		public void AddEmitter(FluidEmitter3D emitter) {

			if (!m_initialized)
				Initialize();

			m_emitters.Add(emitter);
		}

		public void RemoveEmitter(FluidEmitter3D emitter) {

			if (!m_emitters.Contains(emitter))
				return;

			m_emitters.Remove(emitter);
		}

		#endregion
	}
}






























