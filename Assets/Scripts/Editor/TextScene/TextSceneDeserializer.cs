/// 
///  Copyright (c) 2010 TerraVision AS
///  See LICENSE file for licensing details
///
/// TODO:	*Ensure that nested, complex objects don't blow up the entire scenefile.
/// 		*Clean up and comment - subroutine each "type" (primitive, complex, scenelink)
/// 		*Make the Load functions return a bool instead of a string, since the loading is
/// 		now somewhat async - the callback passed to Load should provide the caller with
/// 		the binary name (the scene isn't fully loaded when Load returns, the TextSceneMonitor
/// 		handles the rest).

using UnityEngine;
using UnityEditor;

using System.Xml.Serialization;
using System.IO;
using System.Reflection;
using System.Collections.Generic;

/// <summary>
/// Class reading and populating a scene based on text-based scene files.
/// </summary>
public class TextSceneDeserializer
{
	public delegate void TempSceneSaved();
	
	private enum Pass
	{
		CreateObjects = 0,
		ValueAssignment
	}
	
	private Pass currentPass;
	
	private Queue<GameObject> gameObjects;
	private Queue<Component> gameComponents;
	
	private Dictionary<string, Mesh> builtinMesh;
	private Material builtinMaterial;
	
	private GameObject container;
	private string recursionGuard = "";
	
	
	/// <summary>
	/// Loads a TextScene into a clean scene. Will ask user for path. 
	/// </summary>
	public static void Load()
	{
        string path = EditorUtility.OpenFilePanel("Load scene file", Application.dataPath, "txt");
		
		TextSceneDeserializer.LoadSafe(path);
	}
	
	/// <summary>
	/// Loads the currently selected TextScene. Will fail and notify user if the selected file is
	/// not a text file. 
	/// </summary>
	public static void LoadContext()
	{
		if (Selection.activeObject == null)
		{
			EditorUtility.DisplayDialog ("Nothing selected", "You need to select a text scene asset to load one", "OK");	
			return;
		}
		
		TextAsset asset = Selection.activeObject as TextAsset;
		
		string assetPath = "";
		
		if (asset != null)
			assetPath = AssetDatabase.GetAssetPath(asset);
		
		if (!assetPath.EndsWith(".txt"))
			EditorUtility.DisplayDialog ("Not a text file", "Text scenes can only be loaded from TextAssets (*.txt)", "OK");	
		else
		{
			string fullPath = Application.dataPath 
				+ assetPath.Substring(assetPath.IndexOf('/'));
			
			TextSceneDeserializer.LoadSafe(fullPath);
		}
	}
	
	/// <summary>
	/// Additively loads a TextScene. All objects from the TextScene will be children of
	/// the passed parent, if any. The TextScene will be added with a link to its original
	/// source asset, so it can easily be treated as a prefab, or scene-in-scene link.
	/// </summary>
	public static void LoadAdditive(TextAsset asset, GameObject parent)
	{
		string assetPath = "";
		
		if (asset != null)
			assetPath = AssetDatabase.GetAssetPath(asset);
		
		if (!assetPath.EndsWith(".txt"))
			EditorUtility.DisplayDialog ("Not a text file", "Text scenes can only be loaded from TextAssets (*.txt)", "OK");	
		else
		{
			string fullPath = Application.dataPath 
				+ assetPath.Substring(assetPath.IndexOf('/'));
			
			if (fullPath.ToLower() == TextSceneMonitor.Instance.GetCurrentScene().ToLower())
			{
				EditorUtility.DisplayDialog("ERROR", "Adding this as a TextScene object (" + assetPath + ") would cause an infinite circular dependency", "OK");
				return;
			}
			
			//string name = assetPath.Substring(assetPath.LastIndexOf('/'));

            string spawnPath = '/' + asset.name;

			if (parent != null)
			{
				string uniqueParentPath = Helper.GetFullName(parent);
				
				//Make sure there will be no confusion regarding the parent path.
				GameObject[] gos = Helper.FindGameObjectsFromFullName(uniqueParentPath);
				
				if (gos.Length > 1)
				{
					EditorUtility.DisplayDialog("ERROR", "There are multiple objects that have the path " + uniqueParentPath + ". Please rename your parent object, or create a new, uniquely named one", "OK");
					return;
				}

                spawnPath = uniqueParentPath + spawnPath;
			}

            //"Random" name until we get the links set up.
            //FIXME: Make sure this is unbreakable :S
			GameObject go = new GameObject("TextScene-" + EditorApplication.timeSinceStartup);
			
			go.AddComponent<TextSceneObject>().textScene = asset;
			
			(new TextSceneDeserializer(go, TextSceneMonitor.Instance.GetCurrentScene())).LoadScene(fullPath);
			
			if (parent != null)
			{
				go.transform.parent = parent.transform;
				go.transform.localPosition = Vector3.zero;
				go.transform.localRotation = Quaternion.identity;
			}

            //If we collide with another path, create a new with a suffix
            int suffix = 1;

            string suffixedSpawnPath = spawnPath;
            string suffixedName = asset.name;

            while (Helper.FindGameObjectsFromFullName(suffixedSpawnPath).Length > 0)
            {
                suffixedName = asset.name + suffix;

                suffixedSpawnPath = spawnPath + suffix;

                suffix++;
            }

            go.name = suffixedName;

			Selection.activeObject = go;
		}
	}
	
	/// <summary>
	/// Loads a new scene from path. Path is absolute.
	/// </summary>
	public static string Load(string path)
	{
		return Load(path, null);
	}
	
	public static string Load(string path, TempSceneSaved callback)
	{
		return (new TextSceneDeserializer()).LoadNewScene(path, callback);
	}
	
	public static string LoadSafe(string path)
	{
		//Check if the user wants to save his current work
		if (EditorApplication.SaveCurrentSceneIfUserWantsTo())
		{
			TextSceneMonitor.Instance.SaveIfTempIsNewer();
		}
		else
		{
			Debug.Log("Cancelled TextScene load");
			return "";
		}
		
		return Load(path);
	}
	
	private TextSceneDeserializer()
	{
		
	}
	
	private TextSceneDeserializer(GameObject container)
	{
		this.container = container;
	}
	
	private TextSceneDeserializer(GameObject container, string recursionGuard)
	{
		this.container = container;
		this.recursionGuard = recursionGuard;
	}
	
	public string LoadNewScene(string path)
	{
		return LoadNewScene(path, null);
	}
	
    //TODO: Return bool instead of string. Pass the binary temp scene path with the callback.
    public string LoadNewScene(string path, TempSceneSaved callback)
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            EditorUtility.DisplayDialog("Game is running", "You cannot load in Play-mode", "OK");
            return "";
        }

        if (path == null || path.Length == 0)
            return "";
		
		string startPath = Application.dataPath;
		
        Debug.Log("Opening scene file: " + path + " current: " + EditorApplication.currentScene);

        //Make sure the file exists
        if (!File.Exists(path))
        {
            Debug.LogError("File does not exist: " + path);
            return "";
        }

        EditorApplication.NewScene();

        Transform[] sceneObjects = Helper.GetObjectsOfType<Transform>();

        foreach (Transform o in sceneObjects)
        {
            if (o != null && o.parent == null)
            {
                Debug.Log("Destroying object: " + o.name);
                GameObject.DestroyImmediate(o.gameObject);
            }
        }

		recursionGuard = path;
		
        LoadScene(path);
		

		string tmpScenePath = startPath.Substring(0, startPath.LastIndexOf('/'));
		
		string assetPath = path.Replace(startPath, "");
		
		string[] subFolders = assetPath.Split('/');
		
		Debug.Log("tmpScenePath: " + tmpScenePath + " assetPath: " + assetPath);
		
		//First, make sure we have a temp scenes folder
		tmpScenePath += "/TempScenes";
		
		if (!Directory.Exists(tmpScenePath))
			Directory.CreateDirectory(tmpScenePath);
		
		//Laat entry is the filename itself.
		for (int i = 0; i < subFolders.Length-1; i++)
		{
			tmpScenePath += subFolders[i] + "/";
			
			if (!Directory.Exists(tmpScenePath))
				Directory.CreateDirectory(tmpScenePath);
		}
		
		string fileName = subFolders[subFolders.Length-1];
		
		fileName = fileName.Replace(".txt", ".unity");
		
		string scenePath = tmpScenePath + fileName;
		
		
		Debug.Log("Saving binary unity scene at path: " + scenePath + " (" + path + ")");
		
		//EditorApplication.SaveScene(scenePath);
		//EditorApplication.OpenScene(scenePath);
		
		//TextSceneMonitor.Instance.SetCurrentScene(path);
		
		
		//FIXME: Bugz0rs: Need to delay the save & reload or else prefabs will behave weirdly,
		//       such as getting name reset if doing changes on the prefab and position reset
		//		 to prefab pos if hitting revert.
		TextSceneMonitor.Instance.DoSaveAndReload(scenePath, callback);
		
		return scenePath;
    }
	
    /// <summary>
    /// Loads objects from TextScene file into the currently open scene.
    /// </summary>
	public void LoadScene(string path)
	{
		StreamReader fileStream = File.OpenText(path);


		gameObjects = new Queue<GameObject>();
		gameComponents = new Queue<Component>();
		
		SetupBuiltinMeshes();
		
	    //We're doing this in two passes - first pass simply creates all objects and
        //components, the second pass resolves links and assigns them (the second pass
        //is necessary for in-scene links, so we're just handling everything in there,
        //even prefab and other asset links).
		currentPass = Pass.CreateObjects;
		Deserialize(fileStream, container);

		fileStream.Close();

        GameObject[] gameObjectArray = gameObjects.ToArray();
        Component[] gameComponentArray = gameComponents.ToArray();
		
		fileStream = File.OpenText(path);
		
		currentPass = Pass.ValueAssignment;
		Deserialize(fileStream, container);
		
        fileStream.Close();
		 


        //Don't let the user edit scene-in-scenes (aka prefabs), since
        //we currently have no way of applying changes.
        if (container != null)
        {
            foreach (GameObject go in gameObjectArray)
                go.hideFlags = HideFlags.NotEditable;

            foreach (Component comp in gameComponentArray)
                comp.hideFlags = HideFlags.NotEditable;
        }
	}
	
	/// <summary>
	/// Instantiates and destroys the built-in primitives so we can get a reference to their meshes.
	/// FIXME: It would be a lot better if we could get these mesh references in a less
	///        hack-ish way! 
	/// </summary>
	private void SetupBuiltinMeshes()
	{
		builtinMesh = new Dictionary<string, Mesh>();
		
		GameObject tmp = GameObject.CreatePrimitive(PrimitiveType.Cube);
		builtinMesh.Add("Cube", tmp.GetComponent<MeshFilter>().sharedMesh);
			
		GameObject.DestroyImmediate(tmp);
		
		tmp = GameObject.CreatePrimitive(PrimitiveType.Sphere);
		builtinMesh.Add("Sphere", tmp.GetComponent<MeshFilter>().sharedMesh);
			
		GameObject.DestroyImmediate(tmp);
		
		tmp = GameObject.CreatePrimitive(PrimitiveType.Plane);			
		builtinMesh.Add("Plane", tmp.GetComponent<MeshFilter>().sharedMesh);
			
		GameObject.DestroyImmediate(tmp);
		
		tmp = GameObject.CreatePrimitive(PrimitiveType.Capsule);			
		builtinMesh.Add("Capsule", tmp.GetComponent<MeshFilter>().sharedMesh);
			
		GameObject.DestroyImmediate(tmp);
		
		tmp = GameObject.CreatePrimitive(PrimitiveType.Cylinder);			
		builtinMesh.Add("Cylinder", tmp.GetComponent<MeshFilter>().sharedMesh);
		
		builtinMaterial = tmp.GetComponent<MeshRenderer>().sharedMaterial;
		
		GameObject.DestroyImmediate(tmp);
	}

	/// <summary>
	/// Main function for deserializing the scene from file.  
	/// </summary>
    private void Deserialize(StreamReader stream, GameObject parent)
    {
        while (!stream.EndOfStream)
        {
            string line = stream.ReadLine().Trim();

            ProcessLine(stream, line, parent);
        }
    }

    private void ProcessLine(StreamReader stream, string line, GameObject parent)
    {
        if (line.StartsWith("gameobject"))
        {
            DeserializeGameObject(stream, line.Substring(line.IndexOf(' ')).Trim(), parent);
        }
        else if (line.StartsWith("prefab"))
        {
            DeserializePrefab(stream, line.Substring(line.IndexOf(' ')).Trim(), parent);
        }
		else if (line.StartsWith("textscene"))
        {
            DeserializeTextScene(stream, line.Substring(line.IndexOf(' ')).Trim(), parent);
        }
    }

    private void DeserializeTransform(StreamReader stream, Transform transform)
    {
        string position = stream.ReadLine().Trim();
        string rotation = stream.ReadLine().Trim();
        string localScale = stream.ReadLine().Trim();

        string[] floats = position.Split();

        transform.localPosition = new Vector3(
            float.Parse(floats[0]),
            float.Parse(floats[1]),
            float.Parse(floats[2])
            );

        floats = rotation.Split();

        transform.localRotation = new Quaternion(
            float.Parse(floats[0]),
            float.Parse(floats[1]),
            float.Parse(floats[2]),
            float.Parse(floats[3])
            );

        floats = localScale.Split();

        transform.localScale = new Vector3(
            float.Parse(floats[0]),
            float.Parse(floats[1]),
            float.Parse(floats[2])
            );
    }
	
	private void DeserializeTextScene(StreamReader stream, string name, GameObject parent)
    {
		string guid = stream.ReadLine().Trim().Split()[2];
		
        string assetPath = AssetDatabase.GUIDToAssetPath(guid);

        string fullPath = EditorHelper.GetProjectFolder() + assetPath;

        GameObject go = null;
          
		if (currentPass == Pass.CreateObjects)
		{
			//Random initial name until the local links have been set up.
			//FIXME: I can imagine this random stuff can blow up at any point when we least need it.
			//go = new GameObject(Random.Range(10000, 100000).ToString());
			go = new GameObject(name);
			go.AddComponent<TextSceneObject>().textScene = AssetDatabase.LoadAssetAtPath(assetPath, typeof(TextAsset)) as TextAsset;
			
			//Debug.Log("Full: " + fullPath + " recguard: " + recursionGuard);
			
			string fullName = "/" + name;
			
			if (parent != null)
			{
				fullName = Helper.GetFullName(parent) + fullName;
			}
			
			if (fullPath.ToLower() == recursionGuard.ToLower())
			{
				EditorUtility.DisplayDialog("Recursion guard", "Loading this TextScene (" + assetPath + ") into the current TextScene will throw you into an infinite recursion. Please remove the TextScene object (" + fullName + ") or change the TextScene it points to so it no longer results in a loop", "OK");
			}
			else
				(new TextSceneDeserializer(go, recursionGuard)).LoadScene(fullPath);
			
			gameObjects.Enqueue(go);
		}
		else
		{
			go = gameObjects.Dequeue();
		}
		
		go.name = name;
		
        if (parent != null)
            go.transform.parent = parent.transform;

        DeserializeTransform(stream, go.transform);
    }

    private void DeserializePrefab(StreamReader stream, string name, GameObject parent)
    {
        //Debug.Log("Loading Prefab: " + name);
		
		string guid = stream.ReadLine().Trim().Split()[2];
		
        string assetPath = AssetDatabase.GUIDToAssetPath(guid);

        //Debug.Log("Asset path: " + assetPath);

        GameObject prefab = AssetDatabase.LoadAssetAtPath(assetPath, typeof(GameObject)) as GameObject;

        if (prefab == null)
        {
            Debug.LogError("Failed to load asset: " + assetPath);
            return;
        }

        GameObject go = null;
        
        
		if (currentPass == Pass.CreateObjects)
		{
			go = EditorUtility.InstantiatePrefab(prefab) as GameObject;
			gameObjects.Enqueue(go);
		}
		else
			go = gameObjects.Dequeue();
		
		go.name = name;
		
        if (parent != null)
            go.transform.parent = parent.transform;

        DeserializeTransform(stream, go.transform);
    		
    
    	
    		/*
    		//Had to try to see if this was related to save/load prefab bugs (336621)
    
		//EditorUtility.ResetGameObjectToPrefabState(go);	
    		
		
					
		SerializedObject serObj = new SerializedObject(go);
		
		SerializedProperty nameProp = serObj.FindProperty("m_Name");
		//SerializedProperty localPosX = serObj.FindProperty("m_LocalPosition.x");
		//SerializedProperty localPosY = serObj.FindProperty("m_LocalPosition.y");
		//SerializedProperty localPosZ = serObj.FindProperty("m_LocalPosition.z");
		
		nameProp.stringValue = name;
		//localPosX.floatValue = go.transform.localPosition.x;	
		//localPosY.floatValue = go.transform.localPosition.y;	
		//localPosZ.floatValue = go.transform.localPosition.z;	
		nameProp.serializedObject.ApplyModifiedProperties();
		
		serObj.ApplyModifiedProperties();*/
	}
		
	private void DeserializeGameObject(StreamReader stream, string name, GameObject parent)
    {
        //Debug.Log("Loading GameObject: " + name);

        GameObject go = null;
        
        
		if (currentPass == Pass.CreateObjects)
		{
			go = new GameObject();
			gameObjects.Enqueue(go);
		}
		else
			go = gameObjects.Dequeue();
			
		go.name = name;
		
        if (parent != null)
		{
			//Debug.Log("Parent: " + parent.name);
            go.transform.parent = parent.transform;
		}
		else
		{
			//Debug.Log("Parent: null");
		}
		
		string tagLayerLine = stream.ReadLine();
		
		string[] tagLayerLineElements = tagLayerLine.Trim().Split();
		
		go.tag = tagLayerLineElements[1];
		go.layer = int.Parse(tagLayerLineElements[3]);

        DeserializeTransform(stream, go.transform);

        string children = stream.ReadLine().Trim();

        if (children.Contains("children"))
        {
            int childrenCount = int.Parse(children.Split()[1]);

            for (int i = 0; i < childrenCount; i++)
            {
                string child = stream.ReadLine().Trim();

                ProcessLine(stream, child, go);
            }
        }
		
		string components = stream.ReadLine().Trim();
		
		if (components.Contains("components"))
		{
			int componentCount = int.Parse(components.Split()[1]);
			
			//TODO: Subroutine each component read.
			for (int c = 0; c < componentCount; c++)
			{
				string line = stream.ReadLine().Trim();
				
				//Debug.Log("Reading component: " + line);
				
				string[] componentSplit = line.Trim().Split();
				
				string componentName = componentSplit[1];
				
				int fieldCount = int.Parse(componentName);
				
				Component comp = null;
				
				if (currentPass == Pass.CreateObjects)
				{
					comp = go.AddComponent(componentSplit[0]);
					gameComponents.Enqueue(comp);	
				}	
				else
					comp = gameComponents.Dequeue();
					
				for (int i = 0; i < fieldCount; i++)
				{
					ProcessValueLine(stream, componentName, comp);
				}
			}
		}
    }
	
	private bool ProcessValueLine(StreamReader stream, string name, object obj)
	{
		//TODO: Rename to 'member'
		string field = stream.ReadLine().Trim();
		
		//Debug.Log("Reading member: " + field);
		
		string[] fieldElements = field.Split();
		
		string fieldType = fieldElements[0];
		string fieldName = fieldElements[1];
		string fieldEditorType = fieldElements[2];
		string fieldObjectType = fieldElements[3];
		string fieldValue = field.Substring(field.LastIndexOf('=')+1).Trim();
		
		//Debug.Log("Reading component member: " + field);
		
		object parameter = null;
		
		if (ReadComponentValue(stream, fieldEditorType, fieldObjectType, fieldValue, ref parameter) == false)
			return false;
		
	
		if (obj == null)
		{
			Debug.LogWarning("Skipping member for null object: " + name);
			return false;
		}
		
		
		if (currentPass == Pass.ValueAssignment)
			AssignValue(obj, fieldType, fieldName, parameter);
		
		return true;
	}
	
	private void AssignValue(object comp, string fieldType, string fieldName, object parameter)
	{
		try
		{
			if (fieldType == "field")
			{
				FieldInfo fi = comp.GetType().GetField(fieldName);
			
				if (fi != null)
				{
					fi.SetValue(comp, parameter);
				}
				else
					Debug.LogWarning("Failed to find field (" + fieldName + ") on object: " + comp.GetType().ToString());
			}
			else if (fieldType == "property")
			{
				PropertyInfo pi = comp.GetType().GetProperty(fieldName);
				
				if (pi != null)
				{
					MethodInfo mi = pi.GetSetMethod();
					
					if (mi != null)
					{
						object[] paramList = {parameter};
						
						//Debug.Log("Set property: " + fieldName);
						
						mi.Invoke(comp, paramList);
					}
					else
					{
						Debug.LogWarning("No setter for property: " + fieldName);
					}
				}
				else
					Debug.LogWarning("Failed to find property (" + fieldName + ") on object: " + comp.GetType().ToString());
				
			}
			else
				Debug.LogWarning("Assignment of '" + fieldName + "' failed - invalid field type: " + fieldType);
		}
		catch (System.Exception e)
		{
			Debug.LogWarning("Assignment of '" + fieldName + "' failed. Did it change type since the TextScene was saved? " + e.ToString());
		}
	}

    private static float ConvertFloat(string s)
    {
        try
        {
            return float.Parse(s);
        }
        catch
        {
            if (s.ToLower() == "infinity")
                return float.PositiveInfinity;
        }

        return float.NaN;
    }

	private bool ReadComponentValue(StreamReader stream, string fieldEditorType, string fieldObjectType, string fieldValue, ref object parameter)
	{				
		if (fieldEditorType == "asset")
		{
			System.Type type = Assembly.GetAssembly(typeof(UnityEngine.Object)).GetType(fieldObjectType);
			
			if (type == null)
			{
				//FIXME: Hacky way to get assembly?
				type = Assembly.GetAssembly(typeof(TextSceneObject)).GetType(fieldObjectType);
				
				if (type == null)
				{
					Debug.LogError("Failed to get asset type: " + fieldObjectType);
					return false;
				}
			}
			
			
			if (fieldValue.Length > 0)
			{
				string[] assetParts = fieldValue.Split(',');
				
				//We'll use the GUID instead of the filename to get the asset.
				if (assetParts.Length == 3)
				{
					string guid = assetParts[2].Trim();
					
					Object[] assets = AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GUIDToAssetPath(guid));
					
					string assetName = assetParts[1].Trim();
				
					foreach(Object asset in assets)
					{
						if (asset.GetType() == type && asset.name == assetName)
						{
							parameter = asset;
							break;
						}
					}
				}
			}
		}
		else if (fieldEditorType == "scenelink")
		{
			System.Type type = Assembly.GetAssembly(typeof(UnityEngine.Object)).GetType(fieldObjectType);
			
			if (type == null)
			{
				//FIXME: Hacky way to get assembly?
				type = Assembly.GetAssembly(typeof(TextSceneObject)).GetType(fieldObjectType);
				
				if (type == null)
				{
					Debug.LogError("Failed to get object type: " + fieldObjectType);
					return false;
				}
			}
			
			
			if (currentPass == Pass.ValueAssignment && fieldValue.Length > 0)
			{	
				string prefix = container != null ? Helper.GetFullName(container) : "";
				
				string fullName = prefix + fieldValue;
				
				GameObject[] matches = Helper.FindGameObjectsFromFullName(fullName);	
				GameObject go = matches.Length > 0 ? matches[0] : null;
				
				//FIXME: Why on Earth doesn't this work always?
				//       Try it having a reference to /Geometry/box/box_instance2 and
				//       having a second object in the scene at the path
				//       /Geometry/box/box_instance
				//		 Reported unity case #336886 on this.
				//GameObject go = GameObject.Find(fullName);
				
				if (go == null)
				{
					EditorUtility.DisplayDialog("Error", "Unable to find object in scene: '" + fullName + "'", "OK");
					return false;
				}
				else
				{
					if (type.Equals(typeof(GameObject)))
					    parameter = go;
					else
					    parameter = go.GetComponent(type);
				}
			}
		}
		else if (fieldEditorType == "builtinmesh")
		{
			if (currentPass == Pass.ValueAssignment)
			{
				if (builtinMesh.ContainsKey(fieldValue))
					parameter = builtinMesh[fieldValue];
				else
					Debug.LogWarning("Unknown builtin mesh: " + fieldValue);
			}
		}
		else if (fieldEditorType == "builtinmaterial")
		{
			if (currentPass == Pass.ValueAssignment)
			{
				parameter = builtinMaterial;
			}
		}
		else if (fieldEditorType == "primitive")
		{
			System.Type primitiveType = System.Type.GetType(fieldObjectType);
			
			if (primitiveType == null)
			{
				primitiveType = Assembly.GetAssembly(typeof(UnityEngine.Object)).GetType(fieldObjectType);
			}
			
			//Debug.Log("Handling type: " + fieldObjectType);
			
			char [] separators = new char [] {' ', ',', '(', ')' };

            if (typeof(System.Single) == primitiveType)
            {
                parameter = ConvertFloat(fieldValue);
            }
            else if (typeof(System.Int32) == primitiveType)
                parameter = int.Parse(fieldValue);
            else if (typeof(System.Boolean) == primitiveType)
                parameter = bool.Parse(fieldValue);
            else if (typeof(System.String) == primitiveType)
                parameter = fieldValue;
            else if (typeof(UnityEngine.Vector4) == primitiveType)
            {
                string[] v = fieldValue.Split(separators, System.StringSplitOptions.RemoveEmptyEntries);

                parameter = new Vector4(float.Parse(v[0]),
                                              float.Parse(v[1]),
                                              float.Parse(v[2]),
                                              float.Parse(v[3]));
            }
            else if (typeof(UnityEngine.Quaternion) == primitiveType)
            {
                string[] v = fieldValue.Split(separators, System.StringSplitOptions.RemoveEmptyEntries);

                parameter = new Quaternion(float.Parse(v[0]),
                                              float.Parse(v[1]),
                                              float.Parse(v[2]),
                                              float.Parse(v[3]));
            }
            else if (typeof(UnityEngine.Vector3) == primitiveType)
            {
                string[] v = fieldValue.Split(separators, System.StringSplitOptions.RemoveEmptyEntries);

                parameter = new Vector3(float.Parse(v[0]),
                                              float.Parse(v[1]),
                                              float.Parse(v[2]));
            }
            else if (typeof(UnityEngine.Vector2) == primitiveType)
            {
                string[] v = fieldValue.Split(separators, System.StringSplitOptions.RemoveEmptyEntries);

                parameter = new Vector2(float.Parse(v[0]),
                                              float.Parse(v[1]));
            }
            else if (typeof(UnityEngine.Color) == primitiveType)
            {
                string[] v = fieldValue.Split(separators, System.StringSplitOptions.RemoveEmptyEntries);

                parameter = new Color(float.Parse(v[0]),
                                              float.Parse(v[1]),
                                              float.Parse(v[2]),
                                              float.Parse(v[3]));
            }
            else if (typeof(UnityEngine.Rect) == primitiveType)
            {
                string[] v = fieldValue.Split(separators, System.StringSplitOptions.RemoveEmptyEntries);

                parameter = new Rect(float.Parse(v[0]),
                                              float.Parse(v[1]),
                                              float.Parse(v[2]),
                                              float.Parse(v[3]));
            }
            else if (primitiveType.IsEnum)
            {
                string[] names = System.Enum.GetNames(primitiveType);
                System.Array values = System.Enum.GetValues(primitiveType);

                for (int ei = 0; ei < names.Length; ei++)
                {
                    if (names[ei] == fieldValue)
                    {
                        //Debug.Log(primitiveType.ToString() + ": '" + names[ei] + "' set value: '" + fieldValue + "'");
                        parameter = values.GetValue(ei);
                        break;
                    }
                }
            }
            else
                Debug.LogWarning("Unhandled field type: " + fieldObjectType);
		}
		else if (fieldEditorType == "array")
		{
			System.Type arrayType = System.Type.GetType(fieldObjectType);
			
			if (arrayType == null)
				arrayType = Assembly.GetAssembly(typeof(UnityEngine.Object)).GetType(fieldObjectType);
			
			if (arrayType == null)
				return false;
			
			System.Type arrayElementType = arrayType.GetElementType();
			
			parameter = System.Array.CreateInstance(arrayElementType, int.Parse(fieldValue));
		
			
			System.Array arrayEntries = parameter as System.Array;
			
			
			//Debug.Log("Found array: " + arrayElementType.ToString() + " length: " + arrayEntries.Length);
			
			for (int e = 0; e < arrayEntries.Length; e++)
			{
				string arrayEntryLine = stream.ReadLine().Trim();
				
				//Debug.Log("Reading array entry: " + arrayEntryLine);
		
				string[] elements = arrayEntryLine.Split();
		
				string editorType = elements[0];
				string objectType = elements[1];
				string val = arrayEntryLine.Substring(arrayEntryLine.LastIndexOf('=')+1).Trim();
				
				//Debug.Log("Array entry: " + editorType + " " + objectType + " = " + val);
				
				object arrayEntry = null;
				
				ReadComponentValue(stream, editorType, objectType, val, ref arrayEntry);
				
				arrayEntries.SetValue(arrayEntry, e);
			}
		}
		else if (fieldEditorType == "complex")
		{
			System.Type complexType = System.Type.GetType(fieldObjectType);
			
			if (complexType == null)
				complexType = Assembly.GetAssembly(typeof(UnityEngine.Object)).GetType(fieldObjectType);
			
			if (complexType == null)
				return false;
			
			parameter = System.Activator.CreateInstance(complexType);
			
			int members = int.Parse(fieldValue);
			
			//Debug.Log("Reading complex type: " + complexType.ToString() + " members: " + members);
			
			for(int i = 0; i < members; i++)
				ProcessValueLine(stream, fieldObjectType, parameter);
			
			return true;
		}
		else
			return false;
		
		return true;
	}
}