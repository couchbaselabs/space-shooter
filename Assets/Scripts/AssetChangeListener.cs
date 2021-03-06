using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using UnityEngine;

using Couchbase.Lite;
using Couchbase.Lite.Unity;
using Couchbase.Lite.Util;

/// <summary>
/// Listens for changes in the player_data document
/// in regards to the assets used for drawing the
/// spaceship
/// </summary>
public sealed class AssetChangeListener : MonoBehaviour {

	#region Member Variables

	private Replication _pull, _push;	//The sync objects
	private Database _db;				//The database object

	public GameObject defaultShip;		//The ship object to use when no alternate is specified

	#endregion

	#region Public Methods

	/// <summary>
	/// Signals that the game is over
	/// </summary>
	public void GameOver()
	{
		//Unregister listeners and stop replication
		var doc = _db.GetExistingDocument ("player_data");
		if (doc != null) {
			doc.Change -= DocumentChanged;
		}

		if (_pull != null) {
			_pull.Stop ();
			_pull = null;
		}
	}

	#endregion

	#region Private Methods

	private IEnumerator Start () {
		Debug.LogFormat ("Data path = {0}", Application.persistentDataPath);

#if UNITY_EDITOR_WIN
		Log.SetLogger(new UnityLogger());
#endif
		_db = Manager.SharedInstance.GetDatabase ("spaceshooter");
		_pull = _db.CreatePullReplication (GameController.SYNC_URL);
		_pull.Continuous = true;
		_pull.Start ();
		while (_pull != null && _pull.Status == ReplicationStatus.Active) {
			yield return new WaitForSeconds(0.5f);
		}

		var doc = _db.GetExistingDocument ("player_data");
		if (doc != null) {
			//We have a record!  Get the ship data, if possible.
			string assetName = String.Empty;
			if(doc.UserProperties.ContainsKey("ship_data")) {
				assetName = doc.UserProperties ["ship_data"] as String;
			}
			StartCoroutine(LoadAsset (assetName));
		} else {
			//Create a new record
			doc = _db.GetDocument("player_data");
			doc.PutProperties(new Dictionary<string, object> { { "ship_data", String.Empty } });
		}

		doc.Change += DocumentChanged;
		_push = _db.CreatePushReplication (new Uri ("http://127.0.0.1:4984/spaceshooter"));
		_push.Start();
	}

	private void DocumentChanged (object sender, Document.DocumentChangeEventArgs e)
	{
		if (!e.Change.IsCurrentRevision) {
			return;
		}

		object assetName;
		if (!e.Source.UserProperties.TryGetValue ("ship_data", out assetName)) {
			Debug.LogError("Document does not contain value for asset");
			return;
		}

		UnityMainThreadScheduler.TaskFactory.StartNew (() => {
			StartCoroutine (LoadAsset (assetName as String));	
		});
	}

	private void LoadFromPrefab(GameObject prefab, IDictionary<string, object> metadata)
	{
		metadata = metadata ?? new Dictionary<string, object> ();
		var player = GameObject.FindGameObjectWithTag ("Player");
		player.GetComponent<MeshFilter> ().mesh = prefab.GetComponent<MeshFilter> ().sharedMesh;
		player.GetComponent<MeshRenderer> ().materials = prefab.GetComponent<MeshRenderer> ().sharedMaterials;

		float rate_of_fire = PlayerController.DEFAULT_RATE_OF_FIRE;
		if (metadata.ContainsKey ("rate_of_fire")) {
			rate_of_fire = Convert.ToSingle(metadata["rate_of_fire"]);
		}

		player.GetComponent<PlayerController> ().fireRate = rate_of_fire;
	}

	private IEnumerator LoadAsset(string assetName)
	{
		//If the defaultShip is null then we are not in a condition to
		//load assets (the game is being ended, etc)
		if (defaultShip == null) {
			yield break;
		}

		bool useDefault = String.IsNullOrEmpty (assetName);
		Debug.LogFormat ("Loading asset {0}", useDefault ? "default" : assetName);
		if (useDefault) {
			LoadFromPrefab(defaultShip, null);
			yield break;
		}

		//Sanity check:  does document exist?
		var doc = _db.GetExistingDocument (assetName);
		if (doc == null) {
			Debug.LogErrorFormat ("Document {0} does not exist", assetName);
			yield break;
		}

		//Is document the correct type?
		if (!doc.UserProperties.ContainsKey ("type") || !"ship_model".Equals (doc.UserProperties ["type"] as string)) {
			Debug.LogErrorFormat ("Document {0} has incorrect type", assetName);
			yield break;
		}

		//Does it have an attachment?
		var attachment = Enumerable.FirstOrDefault(doc.CurrentRevision.Attachments);
		if (attachment == null) {
			Debug.LogErrorFormat ("Document {0} is corrupt", assetName);
			yield break;
		}

		//Does the attachment asset bundle have an object of the correct type?
		var token = AssetBundle.CreateFromMemory (attachment.Content.ToArray ());
		yield return token;
		var assetBundle = token.assetBundle;
		var assetData = Enumerable.FirstOrDefault(assetBundle.LoadAllAssets<GameObject> ());
		if (assetData == null) {
			Debug.LogErrorFormat ("Invalid asset in document {0}", assetName);
			yield break;
		}

		LoadFromPrefab (assetData, doc.UserProperties);
		assetBundle.Unload (false);
	}

	#endregion

}
