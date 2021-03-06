﻿using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(fileName = "NewColorTheme", menuName = "Color Theme", order = 310)]
public class ColorPalette : ScriptableObject {

  [SerializeField]
  public Color[] colors;

  public Color this[int index] {
    get {
      return colors[index];
    }
    set {
      colors[index] = value;
    }
  }

}
