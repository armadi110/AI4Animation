﻿using UnityEngine;

public class DogAnimation : MonoBehaviour {

	public bool Inspect = false;

	public float TargetBlending = 0.25f;
	public float GaitTransition = 0.25f;
	public float TrajectoryCorrection = 0.75f;

	public Transform Root;
	public Transform[] Joints = new Transform[0];

	public Controller Controller;
	public Character Character;
	public Trajectory Trajectory;
	public PFNN PFNN;

	private float Phase = 0f;
	private Vector3 TargetDirection;
	private Vector3 TargetVelocity;
	private Vector3[] Velocities = new Vector3[0];

	private enum DrawingMode {Scene, Game};

	void Reset() {
		Root = transform;
		Controller = new Controller();
		Character = new Character();
		Character.BuildHierarchy(transform);
		PFNN = new PFNN();
	}

	void Awake() {
		TargetDirection = GetRootDirection();
		TargetVelocity = Vector3.zero;
		Velocities = new Vector3[Joints.Length];
		Trajectory = new Trajectory(GetRootPosition(), GetRootDirection(), Controller.Styles.Length);
		PFNN.Initialise();
	}

	void Start() {
		Utility.SetFPS(60);
	}

	void Update() {	
		//Update Target Direction / Velocity
		TargetDirection = Vector3.Lerp(TargetDirection, Quaternion.AngleAxis(Controller.QueryTurn()*60f, Vector3.up) * Trajectory.GetRoot().GetDirection(), TargetBlending);
		TargetVelocity = Vector3.Lerp(TargetVelocity, (Quaternion.LookRotation(TargetDirection, Vector3.up) * Controller.QueryMove()).normalized, TargetBlending);

		//TODO: Update strafe etc.
		
		//Update Gait
		for(int i=0; i<Controller.Styles.Length; i++) {
			Trajectory.GetRoot().Styles[i] = Utility.Interpolate(Trajectory.GetRoot().Styles[i], Controller.Styles[i].Query(), GaitTransition);
		}
		//FOR DOG ONLY
		//Trajectory.GetRoot().Styles[1] = Mathf.Max(Trajectory.GetRoot().Styles[1] - Trajectory.GetRoot().Styles[2] - Trajectory.GetRoot().Styles[3], 0f);
		//

		//Predict Future Trajectory
		Vector3[] trajectory_positions_blend = new Vector3[Trajectory.GetPointCount()];
		trajectory_positions_blend[Trajectory.GetRootPointIndex()] = Trajectory.GetRoot().GetPosition();

		for(int i=Trajectory.GetRootPointIndex()+1; i<Trajectory.GetPointCount(); i++) {
			float bias_pos = 0.75f;
			float bias_dir = 1.25f;
			float scale_pos = (1.0f - Mathf.Pow(1.0f - ((float)(i - Trajectory.GetRootPointIndex()) / (Trajectory.GetRootPointIndex())), bias_pos));
			float scale_dir = (1.0f - Mathf.Pow(1.0f - ((float)(i - Trajectory.GetRootPointIndex()) / (Trajectory.GetRootPointIndex())), bias_dir));
			float vel_boost = 1f;
			
			float rescale = 1f / (Trajectory.GetPointCount() - (Trajectory.GetRootPointIndex() + 1f));

			trajectory_positions_blend[i] = trajectory_positions_blend[i-1] + Vector3.Lerp(
				Trajectory.Points[i].GetPosition() - Trajectory.Points[i-1].GetPosition(), 
				vel_boost * rescale * TargetVelocity,
				scale_pos);

			Trajectory.Points[i].SetDirection(Vector3.Lerp(Trajectory.Points[i].GetDirection(), TargetDirection, scale_dir));

			for(int j=0; j<Trajectory.Points[i].Styles.Length; j++) {
				Trajectory.Points[i].Styles[j] = Trajectory.GetRoot().Styles[j];
			}
		}
		
		for(int i=Trajectory.GetRootPointIndex()+1; i<Trajectory.GetPointCount(); i++) {
			Trajectory.Points[i].SetPosition(trajectory_positions_blend[i]);
		}

		//Post-Correct Trajectory
		CollisionChecks(Trajectory.GetRootPointIndex()+1);

		if(PFNN.Parameters != null) {
			//Calculate Root
			Transformation currentRoot = new Transformation(Trajectory.GetRoot().GetPosition(), Trajectory.GetRoot().GetRotation());

			//Input Trajectory Positions / Directions
			for(int i=0; i<Trajectory.GetSampleCount(); i++) {
				Vector3 pos = Trajectory.GetSample(i).GetPosition().RelativePositionTo(currentRoot);
				Vector3 dir = Trajectory.GetSample(i).GetDirection().RelativeDirectionTo(currentRoot);
				PFNN.SetInput(i*6 + 0, pos.x);
				PFNN.SetInput(i*6 + 1, pos.y);
				PFNN.SetInput(i*6 + 2, pos.z);
				PFNN.SetInput(i*6 + 3, dir.x);
				PFNN.SetInput(i*6 + 4, dir.y);
				PFNN.SetInput(i*6 + 5, dir.z);
			}

			//Input Trajectory Heights
			for(int i=0; i<Trajectory.GetSampleCount(); i++) {
				PFNN.SetInput(72 + i*2 + 0, Trajectory.GetSample(i).SampleSide(Trajectory.Width/2f).y - currentRoot.Position.y);
				PFNN.SetInput(72 + i*2 + 1, Trajectory.GetSample(i).SampleSide(-Trajectory.Width/2f).y - currentRoot.Position.y);
			}

			//Input Trajectory Gaits
			for (int i=0; i<Trajectory.GetSampleCount(); i++) {
				for(int j=0; j<Trajectory.GetSample(i).Styles.Length; j++) {
					PFNN.SetInput(96 + (i*Trajectory.GetSample(i).Styles.Length) + j, Trajectory.GetSample(i).Styles[j]);
				}
			}

			//Input Previous Bone Positions / Velocities
			Transformation previousRoot = new Transformation(Trajectory.GetPrevious().GetPosition(), Trajectory.GetPrevious().GetRotation());
			for(int i=0; i<Joints.Length; i++) {
				Vector3 pos = Joints[i].position.RelativePositionTo(previousRoot);
				Vector3 vel = Velocities[i].RelativeDirectionTo(previousRoot);
				PFNN.SetInput(180 + i*6 + 0, pos.x);
				PFNN.SetInput(180 + i*6 + 1, pos.y);
				PFNN.SetInput(180 + i*6 + 2, pos.z);
				PFNN.SetInput(180 + i*6 + 3, vel.x);
				PFNN.SetInput(180 + i*6 + 4, vel.y);
				PFNN.SetInput(180 + i*6 + 5, vel.z);
			}

			//Predict
			PFNN.Predict(Phase);

			//PFNN.Output();

			//Update Past Trajectory
			for(int i=0; i<Trajectory.GetRootPointIndex(); i++) {
				Trajectory.Points[i].SetPosition(Trajectory.Points[i+1].GetPosition());
				Trajectory.Points[i].SetDirection(Trajectory.Points[i+1].GetDirection());
				for(int j=0; j<Trajectory.Points[i].Styles.Length; j++) {
					Trajectory.Points[i].Styles[j] = Trajectory.Points[i+1].Styles[j];
				}
			}

			//Update Current Trajectory
			Vector3 translationalVelocity = new Vector3(PFNN.GetOutput(198), 0f, PFNN.GetOutput(199));
			float angularVelocity = PFNN.GetOutput(200);
			float stand_amount = Mathf.Pow(1.0f-Trajectory.GetRoot().Styles[0], 0.25f);
			Trajectory.GetRoot().SetPosition((stand_amount * translationalVelocity).RelativePositionFrom(currentRoot));
			Trajectory.GetRoot().SetDirection(Quaternion.AngleAxis(stand_amount * angularVelocity, Vector3.up) * Trajectory.GetRoot().GetDirection());
			Transformation newRoot = new Transformation(Trajectory.GetRoot().GetPosition(), Trajectory.GetRoot().GetRotation());

			//Update Future Trajectory
			for(int i=Trajectory.GetRootPointIndex()+1; i<Trajectory.GetPointCount(); i++) {
				Trajectory.Points[i].SetPosition(Trajectory.Points[i].GetPosition() + (stand_amount * translationalVelocity).RelativeDirectionFrom(newRoot));
			}

			//Update Future Trajectory
			for(int i=Trajectory.GetRootPointIndex()+1; i<Trajectory.GetPointCount(); i++) {
				//ROOT	1		2		3		4		5
				//.x....x.......x.......x.......x.......x
				int index = i;
				int prevIndex = Trajectory.GetPreviousSample(index).GetIndex();
				int nextIndex = Trajectory.GetNextSample(index).GetIndex();
				float factor = (float)(nextIndex - index) / (float)Trajectory.GetDensity();
				float prevFactor = factor;
				float nextFactor = 1f - factor;
				int prevSampleIndex = prevIndex / Trajectory.GetDensity();
				int nextSampleIndex = nextIndex / Trajectory.GetDensity();

				//Debug.Log("Index: " + index + " Prev Index: " + prevIndex + " Prev Factor: " + prevFactor + " Prev Sample: " + prevSampleIndex + " Next Index: " + nextIndex + " Next Factor: " + nextFactor + " Next Sample: " + nextSampleIndex);

				float prevPosX = PFNN.GetOutput(prevSampleIndex*6 + 0);
				float prevPosZ = PFNN.GetOutput(prevSampleIndex*6 + 2);
				float prevDirX = PFNN.GetOutput(prevSampleIndex*6 + 3);
				float prevDirZ = PFNN.GetOutput(prevSampleIndex*6 + 5);

				float nextPosX = PFNN.GetOutput(nextSampleIndex*6 + 0);
				float nextPosZ = PFNN.GetOutput(nextSampleIndex*6 + 2);
				float nextDirX = PFNN.GetOutput(nextSampleIndex*6 + 3);
				float nextDirZ = PFNN.GetOutput(nextSampleIndex*6 + 5);

				float posX = prevFactor * prevPosX + nextFactor * nextPosX;
				float posZ = prevFactor * prevPosZ + nextFactor * nextPosZ;
				float dirX = prevFactor * prevDirX + nextFactor * nextDirX;
				float dirZ = prevFactor * prevDirZ + nextFactor * nextDirZ;

				Trajectory.Points[i].SetPosition(
					Utility.Interpolate(
						Trajectory.Points[i].GetPosition(),
						new Vector3(posX, 0f, posZ).RelativePositionFrom(newRoot),
						TrajectoryCorrection
						)
					);
				Trajectory.Points[i].SetDirection(
					Utility.Interpolate(
						Trajectory.Points[i].GetDirection(),
						new Vector3(dirX, 0f, dirZ).normalized.RelativeDirectionFrom(newRoot),
						TrajectoryCorrection
						)
					);
			}

			//Post-Correct Trajectory
			CollisionChecks(Trajectory.GetRootPointIndex());
			
			//Compute Posture
			//TODO: Create lookup table to map to character
			Vector3[] positions = new Vector3[Joints.Length];
			//TODO: rotations
			for(int i=0; i<Joints.Length; i++) {			
				Vector3 position = new Vector3(PFNN.GetOutput(72 + i*6 + 0), PFNN.GetOutput(72 + i*6 + 1), PFNN.GetOutput(72 + i*6 + 2));
				Vector3 velocity = new Vector3(PFNN.GetOutput(72 + i*6 + 3), PFNN.GetOutput(72 + i*6 + 4), PFNN.GetOutput(72 + i*6 + 5));
				//positions[i] = Vector3.Lerp(Joints[i].position.RelativePositionTo(currentRoot) + velocity, position, 0.5f).RelativePositionFrom(currentRoot);
				positions[i] = position.RelativePositionFrom(currentRoot);
				Velocities[i] = velocity.RelativeDirectionFrom(currentRoot);
			}
			
			//Update Posture
			Root.position = newRoot.Position;
			Root.rotation = newRoot.Rotation;
			for(int i=0; i<Joints.Length; i++) {
				Joints[i].position = positions[i];
			}

			//Map to Character
			Character.ForwardKinematics(Root);

			/* Update Phase */
			//Phase = Mathf.Repeat(PFNN.GetOutput(201) * 2f*Mathf.PI, 2f*Mathf.PI);
			Phase = Mathf.Repeat(Phase + Time.deltaTime*2f*Mathf.PI, 2f*Mathf.PI);
		}
	}

	private void CollisionChecks(int start) {
		for(int i=start; i<Trajectory.GetPointCount(); i++) {
			float safety = 0.5f;
			Vector3 previousPos = Trajectory.Points[i-1].GetPosition();
			Vector3 currentPos = Trajectory.Points[i].GetPosition();
			Vector3 testPos = previousPos + safety*(currentPos-previousPos).normalized;
			Vector3 projectedPos = Utility.ProjectCollision(previousPos, testPos, LayerMask.GetMask("Obstacles"));
			if(testPos != projectedPos) {
				Vector3 correctedPos = testPos + safety * (previousPos-testPos).normalized;
				Trajectory.Points[i].SetPosition(correctedPos);
			}
		}
	}

	public void SetJoint(int index, Transform t) {
		if(index < 0 || index >= Joints.Length) {
			return;
		}
		Joints[index] = t;
	}

	public void SetJointCount(int count) {
		count = Mathf.Max(0, count);
		if(Joints.Length != count) {
			System.Array.Resize(ref Joints, count);
		}
	}

	public Vector3 GetRootPosition() {
		return Root.position;
	}

	public Vector3 GetRootDirection() {
		return new Vector3(Root.forward.x, 0f, Root.forward.z);
	}

	void OnGUI() {
		GUI.HorizontalSlider(Utility.GetGUIRect(0.45f, 0.05f, 0.1f, 0.05f), Phase, 0f, 2f*Mathf.PI);
		for(int i=0; i<Trajectory.GetRoot().Styles.Length; i++) {
			GUI.Label(Utility.GetGUIRect(0.75f, 0.05f + i*0.05f, 0.05f, 0.05f), Controller.Styles[i].Name);
			GUI.HorizontalSlider(Utility.GetGUIRect(0.8f, 0.05f + i*0.05f, 0.15f, 0.05f), Trajectory.GetRoot().Styles[i], 0f, 1f);
		}
	}

	void OnRenderObject() {
		if(Application.isPlaying) {
			UnityGL.Start();
			UnityGL.DrawLine(Trajectory.GetRoot().GetPosition(), Trajectory.GetRoot().GetPosition() + TargetDirection, 0.05f, 0f, new Color(Utility.Red.r, Utility.Red.g, Utility.Red.b, 0.75f));
			UnityGL.DrawLine(Trajectory.GetRoot().GetPosition(), Trajectory.GetRoot().GetPosition() + TargetVelocity, 0.05f, 0f, new Color(Utility.Green.r, Utility.Green.g, Utility.Green.b, 0.75f));
			UnityGL.Finish();
			Trajectory.Draw();
		}
		
		if(!Application.isPlaying) {
			Character.ForwardKinematics(Root);
		}
		Character.Draw();

		if(Application.isPlaying) {
			UnityGL.Start();
			for(int i=0; i<Joints.Length; i++) {
				Character.Bone bone = Character.FindBone(Joints[i].name);
				if(bone != null) {
					if(bone.Draw) {
						UnityGL.DrawArrow(
							Joints[i].position,
							Joints[i].position + Velocities[i],
							0.75f,
							0.0075f,
							0.05f,
							new Color(0f, 1f, 0f, 0.5f)
						);
					}
				}
			}
			UnityGL.Finish();
		}
	}

	void OnDrawGizmos() {
		if(!Application.isPlaying) {
			OnRenderObject();
		}
	}
	
}