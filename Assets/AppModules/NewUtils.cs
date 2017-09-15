﻿using Leap.Unity;
using Leap.Unity.Query;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public static class NewUtils {

  #region Math

  /// <summary>
  /// Returns the largest component of the input vector.
  /// </summary>
  public static float CompMax(this Vector2 v) {
    return Mathf.Max(v.x, v.y);
  }

  /// <summary>
  /// Returns the largest component of the input vector.
  /// </summary>
  public static float CompMax(this Vector3 v) {
    return Mathf.Max(Mathf.Max(v.x, v.y), v.z);
  }

  /// <summary>
  /// Returns the largest component of the input vector.
  /// </summary>
  public static float CompMax(this Vector4 v) {
    return Mathf.Max(Mathf.Max(Mathf.Max(v.x, v.y), v.z), v.w);
  }

  /// <summary>
  /// Returns the smallest component of the input vector.
  /// </summary>
  public static float CompMin(this Vector2 v) {
    return Mathf.Min(v.x, v.y);
  }

  /// <summary>
  /// Returns the smallest component of the input vector.
  /// </summary>
  public static float CompMin(this Vector3 v) {
    return Mathf.Min(Mathf.Min(v.x, v.y), v.z);
  }

  /// <summary>
  /// Returns the smallest component of the input vector.
  /// </summary>
  public static float CompMin(this Vector4 v) {
    return Mathf.Min(Mathf.Min(Mathf.Min(v.x, v.y), v.z), v.w);
  }

  #endregion

  #region Unity Objects

  /// <summary>
  /// Usage is the same as FindObjectOfType, but this method will also return objects
  /// that are inactive.
  /// 
  /// Use this method to search for singleton-pattern objects even if they are disabled,
  /// but be warned that it's not cheap to call!
  /// </summary>
  public static T FindObjectInHierarchy<T>() where T : UnityEngine.Object {
    T obj = Resources.FindObjectsOfTypeAll<T>().Query().FirstOrDefault();
    if (obj == null) return null;

    #if UNITY_EDITOR
    // Exclude prefabs.
    var prefabType = UnityEditor.PrefabUtility.GetPrefabType(obj);
    if (prefabType == UnityEditor.PrefabType.ModelPrefab
        || prefabType == UnityEditor.PrefabType.Prefab) {
      return null;
    }
    #endif

    return obj;
  }

  #endregion

  #region Math Utils

  /// <summary>
  /// Extrapolates using time values for positions a and b at extrapolatedTime.
  /// </summary>
  public static Vector3 TimedExtrapolate(Vector3 a, float aTime,
                                         Vector3 b, float bTime,
                                         float extrapolatedTime) {
    return Vector3.LerpUnclamped(a, b, extrapolatedTime.MapUnclamped(aTime, bTime, 0f, 1f));
  }

  /// <summary>
  /// Extrapolates using time values for rotations a and b at extrapolatedTime.
  /// </summary>
  public static Quaternion TimedExtrapolate(Quaternion a, float aTime,
                                            Quaternion b, float bTime,
                                            float extrapolatedTime) {
    return Quaternion.SlerpUnclamped(a, b, extrapolatedTime.MapUnclamped(aTime, bTime, 0f, 1f));
  }

  #endregion

  #region List Utils

  public static void EnsureListExists<T>(ref List<T> list) {
    if (list == null) {
      list = new List<T>();
    }
  }

  public static void EnsureListCount<T>(this List<T> list, int count, Func<T> createT, Action<T> deleteT) {
    while (list.Count < count) {
      list.Add(createT());
    }

    while (list.Count > count) {
      T tempT = list[list.Count - 1];
      list.RemoveAt(list.Count - 1);
      deleteT(tempT);
    }
  }

  public static void EnsureListCount<T>(this List<T> list, int count) {
    if (list.Count == count) return;

    while (list.Count < count) {
      list.Add(default(T));
    }

    while (list.Count > count) {
      T tempT = list[list.Count - 1];
      list.RemoveAt(list.Count - 1);
    }
  }

  #endregion

}
