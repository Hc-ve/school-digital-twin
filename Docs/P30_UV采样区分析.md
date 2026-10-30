# P30 第二轮 · UV2 实际采样区亮度分析

> 生成时间：2026-10-30 10:10:24　场景 `SchoolSceneDay`　Unity 2022.3.62f3c1
> 方法：把每张图集降采样到 256×256，按网格 UV2 逐顶点采样，得到该 Renderer **真正拿到的光照量**。

## 一、总账

| 项 | 数值 |
|---|---|
| Renderer 总数 | 2103 |
| 可测 UV 采样区的 | 2004 |
| **UV 采样区亮度 < 0.10（真·全黑）** | **108** |
| UV 采样区亮度 0.10 – 0.25（偏暗） | 770 |
| UV 采样区亮度 ≥ 0.25（正常） | 1126 |
| **UV2 超出 [0,1] 的** | **14** |

## 二、图集均值 vs UV 采样区亮度（对比：证明图集均值不可用作判据）

| 图集 | 图集均值 | 该图集上 Renderer 的 UV 采样区均值 | 差距 |
|---|---|---|---|
| Lightmap-0 | 0.2217 | 0.2266 | +0.0049 |
| Lightmap-1 | 0.4002 | 0.4011 | +0.0009 |
| Lightmap-2 | 0.4461 | 0.4171 | -0.0290 |
| Lightmap-3 | 0.2654 | 0.2555 | -0.0100 |
| Lightmap-4 | 0.2124 | 0.1847 | -0.0277 |
| Lightmap-5 | 0.3190 | 0.2285 | -0.0904 |
| Lightmap-6 | 0.2561 | 0.2503 | -0.0058 |
| Lightmap-7 | 0.3811 | 0.3595 | -0.0216 |
| Lightmap-8 | 0.1579 | 0.1946 | +0.0367 |
| Lightmap-9 | 0.3047 | 0.2834 | -0.0213 |
| Lightmap-10 | 0.1624 | 0.2210 | +0.0586 |
| Lightmap-11 | 0.2427 | 0.2752 | +0.0326 |
| Lightmap-12 | 0.3243 | 0.2925 | -0.0318 |
| Lightmap-13 | 0.2761 | 0.2663 | -0.0097 |
| Lightmap-14 | 0.3380 | 0.3475 | +0.0095 |
| Lightmap-15 | 0.3132 | 0.2951 | -0.0181 |
| Lightmap-16 | 0.3336 | 0.1807 | -0.1529 |
| Lightmap-17 | 0.0692 | 0.0835 | +0.0143 |
| Lightmap-18 | 0.0557 | 0.1242 | +0.0685 |
| Lightmap-19 | 0.0518 | 0.1349 | +0.0831 |
| Lightmap-20 | 0.1138 | 0.0746 | -0.0392 |
| Lightmap-21 | 0.2401 | 0.1782 | -0.0618 |
| Lightmap-22 | 0.1443 | 0.1785 | +0.0342 |

## 三、★ 真·全黑清单（UV 采样区亮度 < 0.10，共 108 条）

| # | 对象路径 | 材质 | 图集 | 图集均值 | **UV采样亮度** | 中心y | UV范围 | UV越界 |
|---|---|---|---|---|---|---|---|---|
| 1 | `SchoolScene/Interior/SportClassProps/Door02_1/door02_1` | <空槽> | -1 | -1.0000 | **0.0000** | 0.00 | [0.00,0.00]-[0.00,0.00] | 否 |
| 2 | `SchoolScene/Interior/SportClassProps/Door02_1/glass` | <空槽> | -1 | -1.0000 | **0.0000** | 0.00 | [0.00,0.00]-[0.00,0.00] | 否 |
| 3 | `[MockPoints]/TwinPoint_BAK_Class2-1_Sensor_Temp_01/Label` | Font Material | -1 | -1.0000 | **0.0000** | 5.94 | [0.00,0.00]-[0.00,0.00] | 否 |
| 4 | `[MockPoints]/TwinPoint_BAK_Class2-1_Sensor_CO2_01/Label` | Font Material | -1 | -1.0000 | **0.0000** | 5.36 | [0.00,0.00]-[0.00,0.00] | 否 |
| 5 | `[MockPoints]/TwinPoint_BAK_Class2-1_AC_Unit_01/Label` | Font Material | -1 | -1.0000 | **0.0000** | 5.97 | [0.00,0.00]-[0.00,0.00] | 否 |
| 6 | `[MockPoints]/TwinPoint_BAK_Class2-1_Door_Entrance_01/Label` | Font Material | -1 | -1.0000 | **0.0000** | 4.69 | [0.00,0.00]-[0.00,0.00] | 否 |
| 7 | `[MockPoints]/TwinPoint_Class2-1_Sensor_Temp_01/Label` | Font Material | -1 | -1.0000 | **0.0000** | 6.93 | [0.00,0.00]-[0.00,0.00] | 否 |
| 8 | `[MockPoints]/TwinPoint_Class2-1_Sensor_CO2_01/Label` | Font Material | -1 | -1.0000 | **0.0000** | 6.93 | [0.00,0.00]-[0.00,0.00] | 否 |
| 9 | `[MockPoints]/TwinPoint_BAK_Class2-1_Light_Main_01/Label` | Font Material | -1 | -1.0000 | **0.0000** | 6.93 | [0.00,0.00]-[0.00,0.00] | 否 |
| 10 | `[MockPoints]/TwinPoint_Class2-1_AC_Unit_01/Label` | Font Material | -1 | -1.0000 | **0.0000** | 6.93 | [0.00,0.00]-[0.00,0.00] | 否 |
| 11 | `[MockPoints]/TwinPoint_Class2-1_Door_Entrance_01/Label` | Font Material | -1 | -1.0000 | **0.0000** | 6.93 | [0.00,0.00]-[0.00,0.00] | 否 |
| 12 | `[MockPoints]/TwinPoint_BAK2_Class2-1_Light_Main_01/Label` | Font Material | -1 | -1.0000 | **0.0000** | 6.93 | [0.00,0.00]-[0.00,0.00] | 否 |
| 13 | `SchoolScene/Exterior/RoadBordure1e (2)` | ConcreteWall5 | 5 | 0.3190 | **0.0029** | -0.40 | [0.00,0.00]-[1.00,0.06] | 否 |
| 14 | `SchoolScene/Exterior/RoadBordure1e (1)` | ConcreteWall5 | 5 | 0.3190 | **0.0029** | -0.40 | [0.00,0.00]-[1.00,0.06] | 否 |
| 15 | `SchoolScene/Exterior/RoadBordure1f (10)` | ConcreteWall5 | 5 | 0.3190 | **0.0067** | -0.40 | [0.00,0.00]-[1.00,0.04] | 否 |
| 16 | `SchoolScene/Exterior/RoadBordure1f` | ConcreteWall5 | 5 | 0.3190 | **0.0067** | -0.40 | [0.00,0.00]-[1.00,0.04] | 否 |
| 17 | `SchoolScene/Exterior/RoadBordure1f (11)` | ConcreteWall5 | 5 | 0.3190 | **0.0067** | -0.40 | [0.00,0.00]-[1.00,0.04] | 否 |
| 18 | `SchoolScene/Exterior/RoadBordure1f (4)` | ConcreteWall5 | 5 | 0.3190 | **0.0067** | -0.40 | [0.00,0.00]-[1.00,0.04] | 否 |
| 19 | `SchoolScene/Exterior/RoadBordure1f (5)` | ConcreteWall5 | 5 | 0.3190 | **0.0067** | -0.40 | [0.00,0.00]-[1.00,0.04] | 否 |
| 20 | `SchoolScene/Exterior/BordersArea/RoadBordure1c (2)` | ConcreteWall5 | 16 | 0.3336 | **0.0070** | -0.40 | [0.00,0.00]-[1.00,0.18] | 否 |
| 21 | `SchoolScene/Exterior/BordersArea/RoadBordure1c (3)` | ConcreteWall5 | 16 | 0.3336 | **0.0070** | -0.40 | [0.00,0.00]-[1.00,0.18] | 否 |
| 22 | `SchoolScene/Exterior/BordersArea (1)/RoadBordure1c` | ConcreteWall5 | 16 | 0.3336 | **0.0070** | -0.40 | [0.00,0.00]-[1.00,0.18] | 否 |
| 23 | `SchoolScene/Exterior/RoadBordure1c` | ConcreteWall5 | 16 | 0.3336 | **0.0070** | -0.40 | [0.00,0.00]-[1.00,0.18] | 否 |
| 24 | `SchoolScene/Interior/Kitchen/KitchenTable02 (2)` | MetallChrome03 | 17 | 0.0692 | **0.0143** | 0.47 | [0.00,0.00]-[0.99,1.00] | 否 |
| 25 | `SchoolScene/Exterior/BordersArea (2)/RoadBordure1e (1)` | ConcreteWall5 | 16 | 0.3336 | **0.0215** | -0.40 | [0.00,0.00]-[1.00,0.06] | 否 |
| 26 | `SchoolScene/Exterior/RoadBordure1e (1)` | ConcreteWall5 | 16 | 0.3336 | **0.0215** | -0.40 | [0.00,0.00]-[1.00,0.06] | 否 |
| 27 | `SchoolScene/Exterior/BordersArea/RoadBordure1d (1)` | ConcreteWall5 | 0 | 0.2217 | **0.0234** | -0.40 | [0.00,0.00]-[1.00,0.10] | 否 |
| 28 | `SchoolScene/Exterior/BordersArea (1)/RoadBordure1d` | ConcreteWall5 | 0 | 0.2217 | **0.0234** | -0.40 | [0.00,0.00]-[1.00,0.10] | 否 |
| 29 | `SchoolScene/Exterior/BordersArea (2)/RoadBordure1d` | ConcreteWall5 | 0 | 0.2217 | **0.0234** | -0.40 | [0.00,0.00]-[1.00,0.10] | 否 |
| 30 | `SchoolScene/Exterior/BordersArea (3)/RoadBordure1d (1)` | ConcreteWall5 | 0 | 0.2217 | **0.0234** | -0.40 | [0.00,0.00]-[1.00,0.10] | 否 |
| 31 | `SchoolScene/Exterior/BordersArea (4)/RoadBordure1d (1)` | ConcreteWall5 | 0 | 0.2217 | **0.0234** | -0.40 | [0.00,0.00]-[1.00,0.10] | 否 |
| 32 | `SchoolScene/Exterior/SportField01` | Default-Material | 20 | 0.1138 | **0.0290** | -0.48 | [0.00,0.00]-[1.00,0.98] | 否 |
| 33 | `SchoolScene/Interior/Kitchen/Poster02_2a` | PosterFood01 \| MetallChrome01 | 0 | 0.2217 | **0.0323** | 2.76 | [0.00,0.00]-[0.99,0.63] | 否 |
| 34 | `SchoolScene/Exterior/BordersArea/RoadBordure1c` | ConcreteWall5 | 7 | 0.3811 | **0.0344** | -0.40 | [0.00,0.00]-[1.00,0.18] | 否 |
| 35 | `SchoolScene/Exterior/BordersArea/RoadBordure1c (5)` | ConcreteWall5 | 7 | 0.3811 | **0.0344** | -0.40 | [0.00,0.00]-[1.00,0.18] | 否 |
| 36 | `SchoolScene/Exterior/BordersArea (1)/RoadBordure1d (1)` | ConcreteWall5 | 7 | 0.3811 | **0.0344** | -0.40 | [0.00,0.00]-[1.00,0.10] | 否 |
| 37 | `SchoolScene/Exterior/BordersArea (2)/RoadBordure1d (1)` | ConcreteWall5 | 7 | 0.3811 | **0.0344** | -0.40 | [0.00,0.00]-[1.00,0.10] | 否 |
| 38 | `SchoolScene/Exterior/BordersArea (2)/RoadBordure1d` | ConcreteWall5 | 7 | 0.3811 | **0.0344** | -0.40 | [0.00,0.00]-[1.00,0.10] | 否 |
| 39 | `SchoolScene/Exterior/SchoolBuilding/ZalTop` | TileArmstrong01_04 | 15 | 0.3132 | **0.0351** | 7.75 | [0.00,0.00]-[1.00,0.43] | 否 |
| 40 | `SchoolScene/Exterior/BordersArea/RoadBordure1c` | ConcreteWall5 | 5 | 0.3190 | **0.0357** | -0.40 | [0.00,0.00]-[1.00,0.18] | 否 |
| 41 | `SchoolScene/Exterior/BordersArea/RoadBordure1c (1)` | ConcreteWall5 | 5 | 0.3190 | **0.0357** | -0.40 | [0.00,0.00]-[1.00,0.18] | 否 |
| 42 | `SchoolScene/Exterior/BordersArea/RoadBordure1c (4)` | ConcreteWall5 | 5 | 0.3190 | **0.0357** | -0.40 | [0.00,0.00]-[1.00,0.18] | 否 |
| 43 | `SchoolScene/Exterior/BordersArea (1)/RoadBordure1c (1)` | ConcreteWall5 | 5 | 0.3190 | **0.0357** | -0.40 | [0.00,0.00]-[1.00,0.18] | 否 |
| 44 | `SchoolScene/Exterior/BordersArea (2)/RoadBordure1c` | ConcreteWall5 | 5 | 0.3190 | **0.0357** | -0.40 | [0.00,0.00]-[1.00,0.18] | 否 |
| 45 | `SchoolScene/Exterior/RoadBordure1c (1)` | ConcreteWall5 | 5 | 0.3190 | **0.0357** | -0.40 | [0.00,0.00]-[1.00,0.18] | 否 |
| 46 | `SchoolScene/Exterior/RoadBordure1c (2)` | ConcreteWall5 | 5 | 0.3190 | **0.0357** | -0.40 | [0.00,0.00]-[1.00,0.18] | 否 |
| 47 | `SchoolScene/Exterior/RoadBordure1c (3)` | ConcreteWall5 | 5 | 0.3190 | **0.0357** | -0.40 | [0.00,0.00]-[1.00,0.18] | 否 |
| 48 | `SchoolScene/Exterior/RoadBordure1c` | ConcreteWall5 | 9 | 0.3047 | **0.0384** | -0.40 | [0.00,0.00]-[1.00,0.18] | 否 |
| 49 | `SchoolScene/Exterior/BuildingKPP2/Door` | ModelBuildingKPP2 | 8 | 0.1579 | **0.0441** | 0.59 | [0.00,0.00]-[0.99,0.99] | 否 |
| 50 | `SchoolScene/Exterior/SchoolBuilding/Border011` | Wood2 | 9 | 0.3047 | **0.0488** | 3.60 | [0.00,0.00]-[1.00,0.08] | 否 |
| 51 | `SchoolScene/Exterior/RoadBordure1f (7)` | ConcreteWall5 | 16 | 0.3336 | **0.0490** | -0.40 | [0.00,0.00]-[1.00,0.04] | 否 |
| 52 | `SchoolScene/Exterior/RoadBordure1f (4)` | ConcreteWall5 | 16 | 0.3336 | **0.0490** | -0.40 | [0.00,0.00]-[1.00,0.04] | 否 |
| 53 | `SchoolScene/Exterior/RoadBordure1f (5)` | ConcreteWall5 | 16 | 0.3336 | **0.0490** | -0.40 | [0.00,0.00]-[1.00,0.04] | 否 |
| 54 | `SchoolScene/Exterior/RoadBordure1f (6)` | ConcreteWall5 | 16 | 0.3336 | **0.0490** | -0.40 | [0.00,0.00]-[1.00,0.04] | 否 |
| 55 | `SchoolScene/Exterior/RoadBordure1f (12)` | ConcreteWall5 | 16 | 0.3336 | **0.0490** | -0.40 | [0.00,0.00]-[1.00,0.04] | 否 |
| 56 | `SchoolScene/Exterior/RoadBordure1f (17)` | ConcreteWall5 | 16 | 0.3336 | **0.0490** | -0.40 | [0.00,0.00]-[1.00,0.04] | 否 |
| 57 | `SchoolScene/Exterior/RoadBordure1f (1)` | ConcreteWall5 | 16 | 0.3336 | **0.0490** | -0.40 | [0.00,0.00]-[1.00,0.04] | 否 |
| 58 | `SchoolScene/Exterior/RoadBordure1f (2)` | ConcreteWall5 | 16 | 0.3336 | **0.0490** | -0.40 | [0.00,0.00]-[1.00,0.04] | 否 |
| 59 | `SchoolScene/Exterior/RoadBordure1f` | ConcreteWall5 | 16 | 0.3336 | **0.0490** | -0.40 | [0.00,0.00]-[1.00,0.04] | 否 |
| 60 | `SchoolScene/Exterior/Conditioner01 (3)/conditioner` | ModelConditioner01 | 10 | 0.1624 | **0.0618** | 3.00 | [0.01,-0.87]-[1.63,0.99] | **是** |
| 61 | `SchoolScene/Exterior/Conditioner01 (4)/conditioner` | ModelConditioner01 | 10 | 0.1624 | **0.0618** | 6.22 | [0.01,-0.87]-[1.63,0.99] | **是** |
| 62 | `SchoolScene/Exterior/BordersArea (2)/RoadBordure1c (1)` | ConcreteWall5 | 0 | 0.2217 | **0.0626** | -0.40 | [0.00,0.00]-[1.00,0.18] | 否 |
| 63 | `SchoolScene/Interior/SportClassProps/Door02_OpenedWood1/door02_glass` | DoorsWindowsPlastic | 3 | 0.2654 | **0.0737** | 1.44 | [0.00,0.00]-[0.61,0.99] | 否 |
| 64 | `SchoolScene/Interior/SportClassProps/Door02Wood1/DoorAnchor/glass` | DoorsWindowsPlastic | 3 | 0.2654 | **0.0737** | 1.43 | [0.00,0.00]-[0.61,0.99] | 否 |
| 65 | `SchoolScene/Exterior/RoadBordure1d (2)` | ConcreteWall5 | 12 | 0.3243 | **0.0764** | -0.40 | [0.00,0.00]-[1.00,0.10] | 否 |
| 66 | `SchoolScene/Exterior/RoadBordure1d` | ConcreteWall5 | 12 | 0.3243 | **0.0764** | -0.40 | [0.00,0.00]-[1.00,0.10] | 否 |
| 67 | `SchoolScene/Exterior/BordersArea/RoadBordure1d` | ConcreteWall5 | 5 | 0.3190 | **0.0787** | -0.40 | [0.00,0.00]-[1.00,0.10] | 否 |
| 68 | `SchoolScene/Exterior/RoadBordure1d` | ConcreteWall5 | 5 | 0.3190 | **0.0787** | -0.40 | [0.00,0.00]-[1.00,0.10] | 否 |
| 69 | `SchoolScene/Exterior/RoadBordure1f (14)` | ConcreteWall5 | 7 | 0.3811 | **0.0790** | -0.40 | [0.00,0.00]-[1.00,0.04] | 否 |
| 70 | `SchoolScene/Interior/Class2-2/TableSchool01 (5)/TableWood` | Wood4 | 3 | 0.2654 | **0.0790** | 4.15 | [0.00,0.00]-[0.97,1.00] | 否 |
| 71 | `SchoolScene/Interior/Class2-2/TableSchool01a/TableWood` | Wood4 | 3 | 0.2654 | **0.0790** | 4.15 | [0.00,0.00]-[0.97,1.00] | 否 |
| 72 | `SchoolScene/Interior/Class1-4/TableSchool01 (5)/TableWood` | Wood4 | 3 | 0.2654 | **0.0790** | 0.59 | [0.00,0.00]-[0.97,1.00] | 否 |
| 73 | `SchoolScene/Interior/ClassComputer/TableGroup (6)/Computer06` | Computer06 | 8 | 0.1579 | **0.0824** | 0.31 | [0.00,0.00]-[0.99,0.93] | 否 |
| 74 | `SchoolScene/Exterior/SchoolBuilding/DirectorTop` | TileArmstrong01_04 | 20 | 0.1138 | **0.0851** | 6.75 | [0.00,0.00]-[0.98,0.86] | 否 |
| 75 | `SchoolScene/DayLightsExterior/RoadLamp2Off/lamp01_1` | MetallPlate03 | 10 | 0.1624 | **0.0865** | 1.09 | [0.16,0.01]-[0.67,0.99] | 否 |
| 76 | `SchoolScene/DayLightsExterior/RoadLamp2Off/lamp01_1` | MetallPlate03 | 10 | 0.1624 | **0.0865** | 1.09 | [0.16,0.01]-[0.67,0.99] | 否 |
| 77 | `SchoolScene/DayLightsExterior/RoadLamp2Off/lamp01_1` | MetallPlate03 | 10 | 0.1624 | **0.0865** | 1.09 | [0.16,0.01]-[0.67,0.99] | 否 |
| 78 | `SchoolScene/DayLightsExterior/RoadLamp2Off/lamp01_1` | MetallPlate03 | 10 | 0.1624 | **0.0865** | 1.09 | [0.16,0.01]-[0.67,0.99] | 否 |
| 79 | `SchoolScene/DayLightsExterior/RoadLamp2Off/lamp01_1` | MetallPlate03 | 10 | 0.1624 | **0.0865** | 1.09 | [0.16,0.01]-[0.67,0.99] | 否 |
| 80 | `SchoolScene/DayLightsExterior/RoadLamp2Off/lamp01_1` | MetallPlate03 | 10 | 0.1624 | **0.0865** | 1.09 | [0.16,0.01]-[0.67,0.99] | 否 |
| 81 | `SchoolScene/Exterior/BordersArea/RoadBordure1b (17)` | ConcreteWall5 | 15 | 0.3132 | **0.0870** | -0.40 | [0.00,0.00]-[1.00,0.33] | 否 |
| 82 | `SchoolScene/Exterior/BordersArea/RoadBordure1b (18)` | ConcreteWall5 | 15 | 0.3132 | **0.0870** | -0.40 | [0.00,0.00]-[1.00,0.33] | 否 |
| 83 | `SchoolScene/Exterior/BordersArea/RoadBordure1b (19)` | ConcreteWall5 | 15 | 0.3132 | **0.0870** | -0.40 | [0.00,0.00]-[1.00,0.33] | 否 |
| 84 | `SchoolScene/Exterior/RoadBordure1f (2)` | ConcreteWall5 | 17 | 0.0692 | **0.0876** | -0.40 | [0.00,0.00]-[1.00,0.04] | 否 |
| 85 | `SchoolScene/Exterior/RoadBordure1f (9)` | ConcreteWall5 | 17 | 0.0692 | **0.0876** | -0.40 | [0.00,0.00]-[1.00,0.04] | 否 |
| 86 | `SchoolScene/Exterior/RoadBordure1f (18)` | ConcreteWall5 | 17 | 0.0692 | **0.0876** | -0.40 | [0.00,0.00]-[1.00,0.04] | 否 |
| 87 | `SchoolScene/Interior/ClassDirector/Pencil01` | Chancellery1 | 0 | 0.2217 | **0.0897** | 4.35 | [0.00,0.00]-[1.00,0.13] | 否 |
| 88 | `SchoolScene/Interior/Class2-1/Pencil02` | Chancellery2 | 0 | 0.2217 | **0.0897** | 4.30 | [0.00,0.00]-[1.00,0.13] | 否 |
| 89 | `SchoolScene/Interior/Class1-2/Pencil02` | Chancellery2 | 0 | 0.2217 | **0.0897** | 0.73 | [0.00,0.00]-[1.00,0.13] | 否 |
| 90 | `SchoolScene/Interior/Class1-3/Pencil02` | Chancellery2 | 0 | 0.2217 | **0.0897** | 0.74 | [0.00,0.00]-[1.00,0.13] | 否 |
| 91 | `SchoolScene/Interior/Class1-4/Pencil02` | Chancellery2 | 0 | 0.2217 | **0.0897** | 0.74 | [0.00,0.00]-[1.00,0.13] | 否 |
| 92 | `SchoolScene/Exterior/FenceMetal02_R/Anchor/FenceMetal02_L` | MetalFence01 | 0 | 0.2217 | **0.0916** | 1.06 | [0.00,0.00]-[0.62,0.99] | 否 |
| 93 | `SchoolScene/Exterior/FenceMetal02_L/Anchor/FenceMetal02_L` | MetalFence01 | 0 | 0.2217 | **0.0916** | 1.06 | [0.00,0.00]-[0.62,0.99] | 否 |
| 94 | `SchoolScene/Interior/ClassTeachers/Table_corner1 (1)/office_desk_top.bmp` | Table_corner1_top | 10 | 0.1624 | **0.0924** | 4.32 | [0.00,0.00]-[0.50,0.98] | 否 |
| 95 | `SchoolScene/DayLightsExterior/RoadLamp2Off/lamp01_1` | MetallPlate03 | 8 | 0.1579 | **0.0946** | 1.09 | [0.16,0.01]-[0.67,0.99] | 否 |
| 96 | `SchoolScene/DayLightsExterior/RoadLamp2Off/lamp01_1` | MetallPlate03 | 8 | 0.1579 | **0.0946** | 1.09 | [0.16,0.01]-[0.67,0.99] | 否 |
| 97 | `SchoolScene/DayLightsExterior/RoadLamp2Off/lamp01_1` | MetallPlate03 | 8 | 0.1579 | **0.0946** | 1.09 | [0.16,0.01]-[0.67,0.99] | 否 |
| 98 | `SchoolScene/DayLightsExterior/RoadLamp2Off/lamp01_1` | MetallPlate03 | 8 | 0.1579 | **0.0946** | 1.09 | [0.16,0.01]-[0.67,0.99] | 否 |
| 99 | `SchoolScene/DayLightsExterior/RoadLamp2Off/lamp01_1` | MetallPlate03 | 8 | 0.1579 | **0.0946** | 1.09 | [0.16,0.01]-[0.67,0.99] | 否 |
| 100 | `SchoolScene/DayLightsExterior/RoadLamp2Off/lamp01_1` | MetallPlate03 | 8 | 0.1579 | **0.0946** | 1.09 | [0.16,0.01]-[0.67,0.99] | 否 |
| 101 | `SchoolScene/DayLightsExterior/RoadLamp2Off/lamp01_1` | MetallPlate03 | 8 | 0.1579 | **0.0946** | 1.09 | [0.16,0.01]-[0.67,0.99] | 否 |
| 102 | `SchoolScene/DayLightsExterior/RoadLamp2Off/lamp01_1` | MetallPlate03 | 8 | 0.1579 | **0.0946** | 1.09 | [0.16,0.01]-[0.67,0.99] | 否 |
| 103 | `SchoolScene/DayLightsExterior/RoadLamp2Off/lamp01_1` | MetallPlate03 | 8 | 0.1579 | **0.0946** | 1.09 | [0.16,0.01]-[0.67,0.99] | 否 |
| 104 | `SchoolScene/DayLightsExterior/RoadLamp2Off/lamp01_1` | MetallPlate03 | 8 | 0.1579 | **0.0946** | 1.09 | [0.16,0.01]-[0.67,0.99] | 否 |
| 105 | `SchoolScene/DayLightsExterior/RoadLamp2Off/lamp01_1` | MetallPlate03 | 8 | 0.1579 | **0.0946** | 1.09 | [0.16,0.01]-[0.67,0.99] | 否 |
| 106 | `SchoolScene/DayLightsExterior/RoadLamp2Off/lamp01_1` | MetallPlate03 | 8 | 0.1579 | **0.0946** | 1.09 | [0.16,0.01]-[0.67,0.99] | 否 |
| 107 | `SchoolScene/Interior/Class1-3/Electro04 (1)` | Electro04 | 0 | 0.2217 | **0.0970** | 0.81 | [0.01,0.00]-[1.00,1.01] | **是** |
| 108 | `SchoolScene/Interior/Class1-3/Electro04` | Electro04 | 0 | 0.2217 | **0.0970** | 0.92 | [0.01,0.00]-[1.00,1.01] | **是** |

## 四、偏暗清单（0.10 – 0.25，共 770 条，按亮度升序）

| # | 对象路径 | 材质 | 图集 | 图集均值 | UV采样亮度 | 中心y | UV越界 |
|---|---|---|---|---|---|---|---|
| 1 | `SchoolScene/Interior/SportRoom2/Bath01/Door3` | PlasticWhite1 \| MetallChrome01 | 12 | 0.3243 | 0.1024 | 1.10 | 否 |
| 2 | `SchoolScene/Interior/SportRoom3/Bath01 (1)/Door01` | PlasticWhite1 \| MetallChrome01 | 12 | 0.3243 | 0.1024 | 1.10 | 否 |
| 3 | `SchoolScene/Interior/SportRoom3/Bath01 (1)/Door2` | PlasticWhite1 \| MetallChrome01 | 12 | 0.3243 | 0.1024 | 1.10 | 否 |
| 4 | `SchoolScene/Interior/SportRoom3/Bath01 (1)/Door3` | PlasticWhite1 \| MetallChrome01 | 12 | 0.3243 | 0.1024 | 1.10 | 否 |
| 5 | `SchoolScene/Exterior/SchoolBuilding/BuildingBottom` | ConcreteClear01 | 9 | 0.3047 | 0.1048 | -0.24 | 否 |
| 6 | `SchoolScene/Interior/Class1-4/Poster01_1a (1)` | MetallChrome01 \| Geography02a | 3 | 0.2654 | 0.1052 | 1.73 | 否 |
| 7 | `SchoolScene/Exterior/BordersArea/RoadBordure1c (6)` | ConcreteWall5 | 15 | 0.3132 | 0.1053 | -0.40 | 否 |
| 8 | `SchoolScene/Interior/ClassTeachers/Poster05_1a` | Wood3 \| PlasticBlack1 \| PosterText01a \| ColorChart01 | 13 | 0.2761 | 0.1063 | 5.10 | 否 |
| 9 | `SchoolScene/Exterior/RoadBordure1f (3)` | ConcreteWall5 | 12 | 0.3243 | 0.1068 | -0.40 | 否 |
| 10 | `SchoolScene/Interior/Toilets/ToiletCabinet01 (1)/ToiletDoor1` | ToiletCabinet01 | 10 | 0.1624 | 0.1076 | 4.72 | 否 |
| 11 | `SchoolScene/Exterior/FenceConcrete1_3 (1)` | ConcreteWall2 | 20 | 0.1138 | 0.1096 | 1.00 | 否 |
| 12 | `SchoolScene/Exterior/SchoolBuilding/PCClassTop` | TileArmstrong01_04 | 10 | 0.1624 | 0.1097 | 3.20 | 否 |
| 13 | `SchoolScene/Interior/Class1-3/Table02 (2)` | Wood4 | 3 | 0.2654 | 0.1113 | 0.49 | 否 |
| 14 | `SchoolScene/Interior/ClassDirector/Paper02` | Chancellery2 | 0 | 0.2217 | 0.1140 | 4.34 | 否 |
| 15 | `SchoolScene/Interior/ClassDirector/Paper01` | Chancellery1 | 0 | 0.2217 | 0.1140 | 4.34 | 否 |
| 16 | `SchoolScene/Interior/ClassDirector/Paper01 (2)` | Chancellery1 | 0 | 0.2217 | 0.1140 | 4.30 | 否 |
| 17 | `SchoolScene/Interior/ClassDirector/Paper02 (2)` | Chancellery2 | 0 | 0.2217 | 0.1140 | 4.30 | 否 |
| 18 | `SchoolScene/Interior/ClassTeachers/Paper01 (1)` | Chancellery1 | 0 | 0.2217 | 0.1140 | 4.34 | 否 |
| 19 | `SchoolScene/Interior/ClassTeachers/Paper02 (1)` | Chancellery2 | 0 | 0.2217 | 0.1140 | 4.34 | 否 |
| 20 | `SchoolScene/Interior/ClassTeachers/Paper03` | Chancellery3 | 0 | 0.2217 | 0.1140 | 4.34 | 否 |
| 21 | `SchoolScene/Interior/ClassTeachers/Paper02 (2)` | Chancellery2 | 0 | 0.2217 | 0.1140 | 4.34 | 否 |
| 22 | `SchoolScene/Interior/ClassTeachers/Paper01 (2)` | Chancellery1 | 0 | 0.2217 | 0.1140 | 4.34 | 否 |
| 23 | `SchoolScene/Interior/ClassTeachers/Paper03 (1)` | Chancellery3 | 0 | 0.2217 | 0.1140 | 4.34 | 否 |
| 24 | `SchoolScene/Interior/ClassTeachers/Paper03 (2)` | Chancellery3 | 0 | 0.2217 | 0.1140 | 4.34 | 否 |
| 25 | `SchoolScene/Interior/ClassTeachers/Paper03 (3)` | Chancellery3 | 0 | 0.2217 | 0.1140 | 4.34 | 否 |
| 26 | `SchoolScene/Interior/ClassTeachers/Paper02 (3)` | Chancellery2 | 0 | 0.2217 | 0.1140 | 4.34 | 否 |
| 27 | `SchoolScene/Interior/ClassTeachers/Paper03 (5)` | Chancellery3 | 0 | 0.2217 | 0.1140 | 4.34 | 否 |
| 28 | `SchoolScene/Interior/ClassTeachers/Paper03 (4)` | Chancellery3 | 0 | 0.2217 | 0.1140 | 4.34 | 否 |
| 29 | `SchoolScene/Interior/Class2-1/Paper01` | Chancellery1 | 0 | 0.2217 | 0.1140 | 4.30 | 否 |
| 30 | `SchoolScene/Interior/Class2-1/Paper03` | Chancellery3 | 0 | 0.2217 | 0.1140 | 4.29 | 否 |
| 31 | `SchoolScene/Interior/Class2-2/Paper01` | Chancellery1 | 0 | 0.2217 | 0.1140 | 4.30 | 否 |
| 32 | `SchoolScene/Interior/Class1-2/Paper01` | Chancellery1 | 0 | 0.2217 | 0.1140 | 0.74 | 否 |
| 33 | `SchoolScene/Interior/Class1-2/Paper03` | Chancellery3 | 0 | 0.2217 | 0.1140 | 0.73 | 否 |
| 34 | `SchoolScene/Interior/Class1-3/Paper01` | Chancellery1 | 0 | 0.2217 | 0.1140 | 0.74 | 否 |
| 35 | `SchoolScene/Interior/Class1-3/Paper03` | Chancellery3 | 0 | 0.2217 | 0.1140 | 0.73 | 否 |
| 36 | `SchoolScene/Interior/Class1-4/Paper03` | Chancellery3 | 0 | 0.2217 | 0.1140 | 0.73 | 否 |
| 37 | `SchoolScene/Interior/ClassComputer/Paper03` | Chancellery3 | 0 | 0.2217 | 0.1140 | 0.74 | 否 |
| 38 | `SchoolScene/Interior/ClassComputer/Paper03 (1)` | Chancellery3 | 0 | 0.2217 | 0.1140 | 0.74 | 否 |
| 39 | `SchoolScene/Interior/Corridor1/Poster04_1a` | Wood3 \| Cork01b \| PosterStickers01 \| ColorChart01 | 11 | 0.2427 | 0.1162 | 1.69 | 否 |
| 40 | `SchoolScene/Interior/Doors/Door01_2L/Door01_2L/door01_2` | DoorsWindowsPlastic | 9 | 0.3047 | 0.1193 | 0.97 | 否 |
| 41 | `SchoolScene/Interior/Doors/Door01_2R/Door01_2R/door01_2` | DoorsWindowsPlastic | 9 | 0.3047 | 0.1193 | 0.97 | 否 |
| 42 | `SchoolScene/Interior/ClassComputer/TableGroup (1)/Computer06` | Computer06 | 10 | 0.1624 | 0.1218 | 0.31 | 否 |
| 43 | `SchoolScene/Interior/ClassComputer/Computer06 (7)` | Computer06 | 10 | 0.1624 | 0.1218 | 0.31 | 否 |
| 44 | `SchoolScene/Interior/ClassComputer/Computer06 (8)` | Computer06 | 10 | 0.1624 | 0.1218 | 0.31 | 否 |
| 45 | `SchoolScene/Interior/ClassComputer/Computer06 (9)` | Computer06 | 10 | 0.1624 | 0.1218 | 0.31 | 否 |
| 46 | `SchoolScene/Interior/ClassComputer/TableGroup (8)/Computer06` | Computer06 | 10 | 0.1624 | 0.1218 | 0.31 | 否 |
| 47 | `SchoolScene/Interior/SportClassProps/SportGates01 (1)/Gates002` | SportGates | 8 | 0.1579 | 0.1233 | 0.98 | 否 |
| 48 | `SchoolScene/Interior/ClassTeachers/Monitor01 (1)` | Monitor01 | 10 | 0.1624 | 0.1237 | 4.56 | 否 |
| 49 | `SchoolScene/Interior/ClassTeachers/Monitor01 (3)` | Monitor01 | 10 | 0.1624 | 0.1237 | 4.56 | 否 |
| 50 | `SchoolScene/Exterior/FenceConcrete1_5 (3)` | ConcreteWall2 | 18 | 0.0557 | 0.1242 | 1.00 | 否 |
| 51 | `TreeShadows/Tree2(Clone)` | Optimized Bark Material \| Optimized Leaf Material | 0 | 0.2217 | 0.1243 | 7.57 | 否 |
| 52 | `TreeShadows/Tree2(Clone)` | Optimized Bark Material \| Optimized Leaf Material | 0 | 0.2217 | 0.1243 | 7.94 | 否 |
| 53 | `SchoolScene/DayLightsExterior/RoadLamp2Off/lamp01_1` | MetallPlate03 | 11 | 0.2427 | 0.1243 | 1.09 | 否 |
| 54 | `SchoolScene/DayLightsExterior/RoadLamp2Off/lamp01_1` | MetallPlate03 | 11 | 0.2427 | 0.1243 | 1.09 | 否 |
| 55 | `SchoolScene/DayLightsExterior/RoadLamp2Off/lamp01_1` | MetallPlate03 | 11 | 0.2427 | 0.1243 | 1.09 | 否 |
| 56 | `SchoolScene/Interior/Class1-3/Electro06 (2)` | Electro06 | 13 | 0.2761 | 0.1251 | 1.70 | 否 |
| 57 | `TreeShadows/Tree3(Clone)` | Optimized Bark Material \| Optimized Leaf Material | 0 | 0.2217 | 0.1263 | 8.16 | 否 |
| 58 | `TreeShadows/Tree3(Clone)` | Optimized Bark Material \| Optimized Leaf Material | 0 | 0.2217 | 0.1263 | 4.10 | 否 |
| 59 | `SchoolScene/Exterior/RoadBordure1f (8)` | ConcreteWall5 | 15 | 0.3132 | 0.1264 | -0.40 | 否 |
| 60 | `SchoolScene/Exterior/RoadBordure1f (13)` | ConcreteWall5 | 15 | 0.3132 | 0.1264 | -0.40 | 否 |
| 61 | `SchoolScene/Exterior/RoadBordure1f (19)` | ConcreteWall5 | 15 | 0.3132 | 0.1264 | -0.40 | 否 |
| 62 | `SchoolScene/Exterior/RoadBordure1f (3)` | ConcreteWall5 | 15 | 0.3132 | 0.1264 | -0.40 | 否 |
| 63 | `SchoolScene/Interior/Class1-4/Board01_1a` | PlasticWhite1 \| Board01a | 13 | 0.2761 | 0.1268 | 1.48 | 否 |
| 64 | `SchoolScene/Interior/SportRoom1/SportInventar01_01 (1)/Wood` | Wood5 | 8 | 0.1579 | 0.1275 | 0.67 | 否 |
| 65 | `SchoolScene/Interior/ActZal/Seats01 (21)/ChamferBox002` | Leather01Red | 8 | 0.1579 | 0.1294 | 4.27 | 否 |
| 66 | `SchoolScene/Interior/ActZal/Seats01 (22)/ChamferBox002` | Leather01Red | 8 | 0.1579 | 0.1294 | 4.27 | 否 |
| 67 | `SchoolScene/Interior/ActZal/Seats01 (18)/ChamferBox002` | Leather01Red | 8 | 0.1579 | 0.1294 | 4.27 | 否 |
| 68 | `SchoolScene/Interior/ClassTeachers/ChairOffice01 (2)/Leather` | Chair_Office2a | 10 | 0.1624 | 0.1300 | 4.24 | 否 |
| 69 | `SchoolScene/Interior/ClassTeachers/ChairOffice01 (4)/Leather` | Chair_Office2a | 10 | 0.1624 | 0.1300 | 4.24 | 否 |
| 70 | `SchoolScene/Interior/ClassTeachers/ChairOffice01 (5)/Leather` | Chair_Office2a | 10 | 0.1624 | 0.1300 | 4.24 | 否 |
| 71 | `SchoolScene/Interior/Class1-3/Electro07` | Electro07 | 3 | 0.2654 | 0.1302 | 1.16 | 否 |
| 72 | `SchoolScene/Interior/Corridor1/Radiator01 (33)` | Radiator01 | 3 | 0.2654 | 0.1313 | 0.28 | 否 |
| 73 | `SchoolScene/Interior/Corridor2/Radiator01 (40)` | Radiator01 | 3 | 0.2654 | 0.1313 | 3.84 | 否 |
| 74 | `SchoolScene/Interior/Corridor2/Radiator01 (41)` | Radiator01 | 3 | 0.2654 | 0.1313 | 3.84 | 否 |
| 75 | `SchoolScene/Interior/Toilets/ToiletCabinet01 (1)/ToiletDoor2` | ToiletCabinet01 | 10 | 0.1624 | 0.1317 | 4.72 | 否 |
| 76 | `SchoolScene/Interior/Toilets/ToiletCabinet01 (1)/ToiletDoor3` | ToiletCabinet01 | 10 | 0.1624 | 0.1317 | 4.72 | 否 |
| 77 | `SchoolScene/Interior/Toilets/ToiletCabinet01 (1)/ToiletDoor4` | ToiletCabinet01 | 10 | 0.1624 | 0.1317 | 4.72 | 否 |
| 78 | `SchoolScene/Interior/Class2-2/TableSchool01 (9)/TableWood` | Wood4 | 8 | 0.1579 | 0.1321 | 4.15 | 否 |
| 79 | `SchoolScene/Interior/Class2-2/TableSchool01 (2)/TableWood` | Wood4 | 8 | 0.1579 | 0.1321 | 4.15 | 否 |
| 80 | `SchoolScene/Interior/Class2-2/TableSchool01 (3)/TableWood` | Wood4 | 8 | 0.1579 | 0.1321 | 4.15 | 否 |
| 81 | `SchoolScene/Interior/Class1-4/TableSchool01 (9)/TableWood` | Wood4 | 8 | 0.1579 | 0.1321 | 0.59 | 否 |
| 82 | `SchoolScene/Interior/Class1-4/TableSchool01 (3)/TableWood` | Wood4 | 8 | 0.1579 | 0.1321 | 0.59 | 否 |
| 83 | `TreeShadows/Tree2(Clone)` | Optimized Bark Material \| Optimized Leaf Material | 10 | 0.1624 | 0.1346 | 5.65 | 否 |
| 84 | `SchoolScene/Exterior/FenceConcrete1_5 (1)` | ConcreteWall2 | 19 | 0.0518 | 0.1349 | 1.00 | 否 |
| 85 | `SchoolScene/Interior/Doors/Door07a (11)` | Door07a \| Doorparts01 | 12 | 0.3243 | 0.1350 | 4.55 | 否 |
| 86 | `SchoolScene/Interior/Doors/Door07a (10)` | Door07a \| Doorparts01 | 12 | 0.3243 | 0.1350 | 4.55 | 否 |
| 87 | `SchoolScene/Interior/Doors/Door07a (1)` | Door07a \| Doorparts01 | 12 | 0.3243 | 0.1350 | 1.01 | 否 |
| 88 | `SchoolScene/Interior/Doors/Door07a` | Door07a \| Doorparts01 | 12 | 0.3243 | 0.1350 | 1.01 | 否 |
| 89 | `SchoolScene/Interior/Doors/Door02 (2)` | Door02 \| Doorparts04 | 12 | 0.3243 | 0.1350 | 4.57 | 否 |
| 90 | `TreeShadows/Tree3(Clone)` | Optimized Bark Material \| Optimized Leaf Material | 8 | 0.1579 | 0.1355 | 5.85 | 否 |
| 91 | `TreeShadows/Tree2(Clone)` | Optimized Bark Material \| Optimized Leaf Material | 8 | 0.1579 | 0.1361 | 7.28 | 否 |
| 92 | `SchoolScene/Interior/Kitchen/KitchenFurniture07` | MetallChrome03 | 5 | 0.3190 | 0.1362 | 2.78 | 否 |
| 93 | `SchoolScene/Interior/Class1-4/Poster06_1a` | Geography01a \| Wood3 \| Default-Material | 13 | 0.2761 | 0.1363 | 1.62 | 否 |
| 94 | `SchoolScene/Exterior/RoadBordure1e` | ConcreteWall5 | 13 | 0.2761 | 0.1378 | -0.40 | 否 |
| 95 | `SchoolScene/Exterior/RoadBordure1e` | ConcreteWall5 | 13 | 0.2761 | 0.1378 | -0.40 | 否 |
| 96 | `SchoolScene/Interior/ClassTeachers/ChairOffice01 (1)/Leather` | Chair_Office2a | 8 | 0.1579 | 0.1395 | 4.24 | 否 |
| 97 | `SchoolScene/Interior/ClassComputer/SubBuffer02` | SubBuffer02 | 0 | 0.2217 | 0.1398 | 2.10 | 否 |
| 98 | `SchoolScene/Interior/ClassComputer/SubBuffer02 (1)` | SubBuffer02 | 0 | 0.2217 | 0.1398 | 2.10 | 否 |
| 99 | `SchoolScene/Interior/Class2-1/TableSchool01 (1)/TableMetall` | MetallPainted01Gray | 10 | 0.1624 | 0.1402 | 3.93 | 否 |
| 100 | `SchoolScene/Interior/Class2-1/TableSchool01a/TableMetall` | MetallPainted01Gray | 10 | 0.1624 | 0.1402 | 3.93 | 否 |
| 101 | `SchoolScene/Interior/Class2-1/TableSchool01 (2)/TableMetall` | MetallPainted01Gray | 10 | 0.1624 | 0.1402 | 3.93 | 否 |
| 102 | `SchoolScene/Interior/Class1-3/TableSchool01b (3)/TableMetall` | PlasticBlack1 | 10 | 0.1624 | 0.1402 | 0.37 | 否 |
| 103 | `SchoolScene/Interior/Class1-3/TableSchool01b (8)/TableMetall` | PlasticBlack1 | 10 | 0.1624 | 0.1402 | 0.37 | 否 |
| 104 | `SchoolScene/Interior/Class1-3/TableSchool01b (9)/TableMetall` | PlasticBlack1 | 10 | 0.1624 | 0.1402 | 0.37 | 否 |
| 105 | `SchoolScene/Interior/Class1-3/TableSchool01b (2)/TableMetall` | PlasticBlack1 | 10 | 0.1624 | 0.1402 | 0.37 | 否 |
| 106 | `SchoolScene/Exterior/FenceConcrete1_5` | ConcreteWall2 | 17 | 0.0692 | 0.1404 | 1.00 | 否 |
| 107 | `SchoolScene/Exterior/ConcreteBox01 (14)` | ConcreteClear03 | 8 | 0.1579 | 0.1406 | -0.18 | 否 |
| 108 | `SchoolScene/Exterior/ConcreteBox01 (15)` | ConcreteClear03 | 8 | 0.1579 | 0.1406 | -0.18 | 否 |
| 109 | `SchoolScene/Exterior/ConcreteBox01 (16)` | ConcreteClear03 | 8 | 0.1579 | 0.1406 | -0.18 | 否 |
| 110 | `SchoolScene/Exterior/ConcreteBox01 (23)` | ConcreteClear03 | 8 | 0.1579 | 0.1406 | -0.18 | 否 |
| 111 | `SchoolScene/Exterior/ConcreteBox01 (24)` | ConcreteClear03 | 8 | 0.1579 | 0.1406 | -0.18 | 否 |
| 112 | `SchoolScene/Exterior/ConcreteBox01 (25)` | ConcreteClear03 | 8 | 0.1579 | 0.1406 | -0.18 | 否 |
| 113 | `SchoolScene/Exterior/SchoolBuilding/ZalStairs` | Wood3 | 5 | 0.3190 | 0.1428 | 3.86 | 否 |
| 114 | `SchoolScene/Interior/Class1-3/Split01c` | Split01c | 8 | 0.1579 | 0.1438 | 2.58 | 否 |
| 115 | `SchoolScene/Interior/Class1-4/Split01a` | Split01a | 8 | 0.1579 | 0.1438 | 2.57 | 否 |
| 116 | `SchoolScene/Interior/Toilets/Toilet01 (4)` | Toilet01 | 10 | 0.1624 | 0.1439 | 4.12 | 否 |
| 117 | `SchoolScene/Interior/Toilets/Toilet01 (5)` | Toilet01 | 10 | 0.1624 | 0.1439 | 4.12 | 否 |
| 118 | `SchoolScene/Interior/Toilets/Toilet01 (7)` | Toilet01 | 10 | 0.1624 | 0.1439 | 4.12 | 否 |
| 119 | `SchoolScene/Exterior/Benches/Bench01_1 (2)/Concrete` | ConcreteClear03 | 8 | 0.1579 | 0.1442 | -0.27 | 否 |
| 120 | `SchoolScene/Exterior/Benches/Bench01_1 (3)/Concrete` | ConcreteClear03 | 8 | 0.1579 | 0.1442 | -0.27 | 否 |
| 121 | `SchoolScene/Exterior/Benches/Bench01_1 (4)/Concrete` | ConcreteClear03 | 8 | 0.1579 | 0.1442 | -0.27 | 否 |
| 122 | `SchoolScene/Exterior/Benches/Bench01_1 (5)/Concrete` | ConcreteClear03 | 8 | 0.1579 | 0.1442 | -0.27 | 否 |
| 123 | `SchoolScene/Exterior/Benches/Bench01_1 (13)/Concrete` | ConcreteClear03 | 8 | 0.1579 | 0.1442 | -0.27 | 否 |
| 124 | `SchoolScene/Exterior/Benches/Bench01_1 (12)/Concrete` | ConcreteClear03 | 8 | 0.1579 | 0.1442 | -0.27 | 否 |
| 125 | `SchoolScene/Interior/Class1-2/Poster01_1a (2)` | MetallChrome01 \| Chemistry01 | 0 | 0.2217 | 0.1450 | 1.68 | 否 |
| 126 | `SchoolScene/Interior/Kitchen/SchoolKitchenSeats (14)/Chair05 (4)` | Leather01Black \| MetallPainted01Gray | 8 | 0.1579 | 0.1452 | 0.45 | 否 |
| 127 | `SchoolScene/Interior/Kitchen/SchoolKitchenSeats (14)/Chair05 (6)` | Leather01Black \| MetallPainted01Gray | 8 | 0.1579 | 0.1452 | 0.45 | 否 |
| 128 | `SchoolScene/Interior/Kitchen/SchoolKitchenSeats (14)/Chair05 (7)` | Leather01Black \| MetallPainted01Gray | 8 | 0.1579 | 0.1452 | 0.45 | 否 |
| 129 | `SchoolScene/Interior/Kitchen/SchoolKitchenSeats (13)/Chair05 (6)` | Leather01Black \| MetallPainted01Gray | 8 | 0.1579 | 0.1452 | 0.45 | 否 |
| 130 | `SchoolScene/Interior/Kitchen/SchoolKitchenSeats (11)/Chair05 (6)` | Leather01Black \| MetallPainted01Gray | 8 | 0.1579 | 0.1452 | 0.45 | 否 |
| 131 | `SchoolScene/Interior/Kitchen/SchoolKitchenSeats (10)/Chair05 (6)` | Leather01Black \| MetallPainted01Gray | 8 | 0.1579 | 0.1452 | 0.45 | 否 |
| 132 | `SchoolScene/Interior/Kitchen/SchoolKitchenSeats (9)/Chair05 (4)` | Leather01Black \| MetallPainted01Gray | 8 | 0.1579 | 0.1452 | 0.45 | 否 |
| 133 | `SchoolScene/Interior/Kitchen/SchoolKitchenSeats (9)/Chair05 (5)` | Leather01Black \| MetallPainted01Gray | 8 | 0.1579 | 0.1452 | 0.45 | 否 |
| 134 | `SchoolScene/Interior/Kitchen/SchoolKitchenSeats (9)/Chair05 (6)` | Leather01Black \| MetallPainted01Gray | 8 | 0.1579 | 0.1452 | 0.45 | 否 |
| 135 | `SchoolScene/Interior/Kitchen/SchoolKitchenSeats (8)/Chair05 (6)` | Leather01Black \| MetallPainted01Gray | 8 | 0.1579 | 0.1452 | 0.45 | 否 |
| 136 | `SchoolScene/Interior/Kitchen/SchoolKitchenSeats (8)/Chair05 (7)` | Leather01Black \| MetallPainted01Gray | 8 | 0.1579 | 0.1452 | 0.45 | 否 |
| 137 | `SchoolScene/Interior/Class1-3/Electro05_2 (1)` | Electro05 | 0 | 0.2217 | 0.1453 | 0.78 | 否 |
| 138 | `SchoolScene/Interior/Class1-3/Electro05_2` | Electro05 | 0 | 0.2217 | 0.1453 | 0.88 | 否 |
| 139 | `SchoolScene/Interior/Corridor2/Poster04_1a (5)` | Wood3 \| Cork01b \| PosterStickers01 \| ColorChart01 | 13 | 0.2761 | 0.1469 | 5.29 | 否 |
| 140 | `SchoolScene/Interior/Class1-3/Capboard01_03/Capboard01` | Wood4 | 8 | 0.1579 | 0.1478 | 1.04 | 否 |
| 141 | `SchoolScene/Interior/Class1-4/Capboard01_03 (1)/Capboard01` | Wood4 | 8 | 0.1579 | 0.1478 | 1.04 | 否 |
| 142 | `SchoolScene/Exterior/Chair06a/Chair06Metal` | PlasticBlack1 | 8 | 0.1579 | 0.1491 | -0.07 | 否 |
| 143 | `SchoolScene/Exterior/RoadBordure1f (15)` | ConcreteWall5 | 9 | 0.3047 | 0.1517 | -0.39 | 否 |
| 144 | `SchoolScene/Exterior/RoadBordure1f (2)` | ConcreteWall5 | 9 | 0.3047 | 0.1517 | -0.40 | 否 |
| 145 | `SchoolScene/Interior/SportRoom2/Bath01/002` | PlasticWhite1 | 10 | 0.1624 | 0.1519 | 0.03 | 否 |
| 146 | `SchoolScene/Interior/Toilets/Toilet01Urinal (1)` | Toilet01 | 10 | 0.1624 | 0.1522 | 4.45 | 否 |
| 147 | `SchoolScene/Interior/Toilets/Toilet01Urinal (2)` | Toilet01 | 10 | 0.1624 | 0.1522 | 4.45 | 否 |
| 148 | `SchoolScene/Interior/Toilets/Toilet01Urinal (3)` | Toilet01 | 10 | 0.1624 | 0.1522 | 4.45 | 否 |
| 149 | `SchoolScene/Exterior/Benches/Bench01_1 (8)/Wood` | Wood2 | 5 | 0.3190 | 0.1535 | -0.08 | 否 |
| 150 | `SchoolScene/Exterior/Benches/Bench01_1 (9)/Wood` | Wood2 | 5 | 0.3190 | 0.1535 | -0.08 | 否 |
| 151 | `SchoolScene/Interior/SportClassProps/SportBasketShit01 (5)/BasketMetal` | MetallPainted01Red \| MetallPainted01Gray | 8 | 0.1579 | 0.1541 | 3.52 | 否 |
| 152 | `SchoolScene/Interior/Class1-3/Table02` | Wood4 | 8 | 0.1579 | 0.1572 | 0.36 | 否 |
| 153 | `SchoolScene/Interior/SportRoom1/SportInventar01_02/Balls` | SportBalls | 10 | 0.1624 | 0.1586 | 0.76 | 否 |
| 154 | `SchoolScene/Exterior/FenceMetal01 (2)/FenceMetal01` | MetallPainted01Green | 13 | 0.2761 | 0.1588 | 2.62 | 否 |
| 155 | `SchoolScene/Interior/Toilets/Sink01TrashBox (1)` | Sink01 | 10 | 0.1624 | 0.1590 | 3.80 | 否 |
| 156 | `SchoolScene/Interior/SportRoom2/Bath01/BathsShower` | MetallChrome01 | 10 | 0.1624 | 0.1590 | 1.39 | 否 |
| 157 | `SchoolScene/Interior/Toilets/ToiletCabinet01 (1)/ToiletCabinet01` | ToiletCabinet01 | 10 | 0.1624 | 0.1604 | 4.80 | 否 |
| 158 | `SchoolScene/Interior/Kitchen/FlowerRoom4a (1)` | FlowerRoom4 | 8 | 0.1579 | 0.1622 | 1.96 | 否 |
| 159 | `SchoolScene/Interior/Kitchen/FlowerRoom4a (2)` | FlowerRoom4 | 8 | 0.1579 | 0.1622 | 1.96 | 否 |
| 160 | `SchoolScene/Interior/Class1-4/FlowerGroup2 (1)/FlowerRoom4a` | FlowerRoom4 | 8 | 0.1579 | 0.1622 | 2.16 | 否 |
| 161 | `SchoolScene/Interior/Kitchen/SchoolKitchenSeats (4)/Chair05 (5)` | Leather01Black \| MetallPainted01Gray | 10 | 0.1624 | 0.1636 | 0.45 | 否 |
| 162 | `SchoolScene/Interior/Kitchen/SchoolKitchenSeats (4)/Chair05 (7)` | Leather01Black \| MetallPainted01Gray | 10 | 0.1624 | 0.1636 | 0.45 | 否 |
| 163 | `SchoolScene/Interior/Class1-4/Blueprints05 (1)` | Paper01 \| Geography01b \| Geography01b \| Geography01b | 13 | 0.2761 | 0.1660 | 0.78 | 否 |
| 164 | `SchoolScene/Interior/SportRoom1/SportKozel02 (1)/Kozel2Leather` | Leather06 | 10 | 0.1624 | 0.1663 | 1.00 | 否 |
| 165 | `SchoolScene/Interior/Corridor1/Poster03_3a (2)/Glass` | PosterText01b | 0 | 0.2217 | 0.1665 | 5.16 | 否 |
| 166 | `SchoolScene/Interior/Corridor1/Poster04_5a` | Wood3 \| PosterBulletin01 \| PosterStickers01 \| ColorChart01 | 9 | 0.3047 | 0.1666 | 1.71 | 否 |
| 167 | `SchoolScene/Exterior/SchoolBuilding/Stairs02Concrete` | ConctereWall12 | 10 | 0.1624 | 0.1671 | 1.78 | 否 |
| 168 | `SchoolScene/DayLightsExterior/RoadLamp1a (1)/LampMetall` | MetallChrome01 | 10 | 0.1624 | 0.1677 | 9.15 | 否 |
| 169 | `SchoolScene/DayLightsExterior/RoadLamp1a (12)/LampMetall` | MetallChrome01 | 10 | 0.1624 | 0.1677 | 9.15 | 否 |
| 170 | `SchoolScene/Interior/Class1-2/TableSchool04/TableWood` | PlasticWhite1 \| MetallChrome01 | 13 | 0.2761 | 0.1688 | 0.77 | 否 |
| 171 | `SchoolScene/Interior/Class1-2/TableSchool04 (3)/TableWood` | PlasticWhite1 \| MetallChrome01 | 13 | 0.2761 | 0.1688 | 0.77 | 否 |
| 172 | `SchoolScene/Interior/SportClassProps/Radiator01 (1)` | Radiator01 | 8 | 0.1579 | 0.1698 | 0.38 | 否 |
| 173 | `SchoolScene/Interior/Corridor1/Radiator01 (11)` | Radiator01 | 8 | 0.1579 | 0.1698 | 0.37 | 否 |
| 174 | `SchoolScene/Interior/Corridor1/Radiator01 (14)` | Radiator01 | 8 | 0.1579 | 0.1698 | 0.37 | 否 |
| 175 | `SchoolScene/Interior/ClassTeachers/Radiator01 (5)` | Radiator01 | 8 | 0.1579 | 0.1698 | 3.86 | 否 |
| 176 | `SchoolScene/Interior/Class2-2/Radiator01 (5)` | Radiator01 | 8 | 0.1579 | 0.1698 | 3.86 | 否 |
| 177 | `SchoolScene/Interior/Class1-3/Radiator01 (6)` | Radiator01 | 8 | 0.1579 | 0.1698 | 0.38 | 否 |
| 178 | `SchoolScene/Interior/Class1-4/Radiator01 (5)` | Radiator01 | 8 | 0.1579 | 0.1698 | 0.39 | 否 |
| 179 | `SchoolScene/Interior/Corridor2/Radiator01 (38)` | Radiator01 | 8 | 0.1579 | 0.1698 | 3.84 | 否 |
| 180 | `SchoolScene/Interior/Kitchen/KitchenRazdacha01_02b` | MetallChrome03 \| PlasticRed1 \| PlasticBlack1 | 11 | 0.2427 | 0.1701 | 0.67 | 否 |
| 181 | `SchoolScene/Interior/Kitchen/KitchenRazdacha01_02b (1)` | MetallChrome03 \| PlasticRed1 \| PlasticBlack1 | 11 | 0.2427 | 0.1701 | 0.67 | 否 |
| 182 | `SchoolScene/Exterior/BordersArea/RoadBordure1e` | ConcreteWall5 | 9 | 0.3047 | 0.1701 | -0.40 | 否 |
| 183 | `SchoolScene/Exterior/Conditioner01 (6)/conditioner` | ModelConditioner01 | 16 | 0.3336 | 0.1704 | 6.22 | **是** |
| 184 | `SchoolScene/Interior/Class1-3/FlowerGroup2 (2)/FlowerRoom4a` | FlowerRoom4 | 10 | 0.1624 | 0.1704 | 2.18 | 否 |
| 185 | `SchoolScene/Interior/Kitchen/Curtain03_2 (4)` | FabricCurtain02b \| MetallChrome01 | 8 | 0.1579 | 0.1707 | 2.63 | 否 |
| 186 | `SchoolScene/Interior/Kitchen/Curtain03_2 (5)` | FabricCurtain02b \| MetallChrome01 | 8 | 0.1579 | 0.1707 | 2.63 | 否 |
| 187 | `SchoolScene/Exterior/RoadBordure1f` | ConcreteWall5 | 2 | 0.4461 | 0.1709 | -0.40 | 否 |
| 188 | `SchoolScene/Exterior/RoadBordure1f (20)` | ConcreteWall5 | 2 | 0.4461 | 0.1709 | -0.40 | 否 |
| 189 | `SchoolScene/DayLightsInterior/LightLum01_03 (4)` | LightLum01 | 3 | 0.2654 | 0.1714 | 7.40 | 否 |
| 190 | `SchoolScene/DayLightsInterior/LightLum01_03 (5)` | LightLum01 | 3 | 0.2654 | 0.1714 | 7.40 | 否 |
| 191 | `SchoolScene/Interior/SportRoom1/SportInventar01_01/Balls` | SportBalls | 10 | 0.1624 | 0.1715 | 0.53 | 否 |
| 192 | `SchoolScene/Interior/Kitchen/SchoolKitchenSeats (16)/TableSimple01_02/TableWood` | Wood3 | 1 | 0.4002 | 0.1717 | 0.80 | 否 |
| 193 | `SchoolScene/Interior/Kitchen/SchoolKitchenSeats (15)/TableSimple01_02/TableWood` | Wood3 | 1 | 0.4002 | 0.1717 | 0.80 | 否 |
| 194 | `SchoolScene/Interior/Kitchen/SchoolKitchenSeats (13)/TableSimple01_02/TableWood` | Wood3 | 1 | 0.4002 | 0.1717 | 0.80 | 否 |
| 195 | `SchoolScene/Interior/Kitchen/FlowerRoom4b (2)` | FlowerRoom4 | 8 | 0.1579 | 0.1723 | 2.33 | 否 |
| 196 | `SchoolScene/Interior/Class2-1/Table02` | Wood4 | 0 | 0.2217 | 0.1726 | 3.93 | 否 |
| 197 | `SchoolScene/Interior/Class1-3/Electro05_1 (1)` | Electro05 | 0 | 0.2217 | 0.1728 | 0.78 | 否 |
| 198 | `SchoolScene/Interior/Class1-3/Electro05_1` | Electro05 | 0 | 0.2217 | 0.1728 | 0.88 | 否 |
| 199 | `SchoolScene/Interior/SportRoom1/Radiator01 (9)` | Radiator01 | 10 | 0.1624 | 0.1733 | 0.28 | 否 |
| 200 | `SchoolScene/Interior/ClassTeachers/Radiator01 (4)` | Radiator01 | 10 | 0.1624 | 0.1733 | 3.86 | 否 |
| 201 | `SchoolScene/Interior/ClassTeachers/Radiator01 (3)` | Radiator01 | 10 | 0.1624 | 0.1733 | 3.86 | 否 |
| 202 | `SchoolScene/Interior/Class2-1/Radiator01 (5)` | Radiator01 | 10 | 0.1624 | 0.1733 | 3.86 | 否 |
| 203 | `SchoolScene/Interior/Class1-3/Radiator01 (4)` | Radiator01 | 10 | 0.1624 | 0.1733 | 0.38 | 否 |
| 204 | `SchoolScene/Interior/Class1-3/Radiator01 (5)` | Radiator01 | 10 | 0.1624 | 0.1733 | 0.38 | 否 |
| 205 | `SchoolScene/Interior/ClassComputer/Radiator01 (8)` | Radiator01 | 10 | 0.1624 | 0.1733 | 0.28 | 否 |
| 206 | `SchoolScene/Exterior/FenceMetal01_1 (3)/FenceMetal01` | MetallPainted01Green | 15 | 0.3132 | 0.1735 | 2.62 | 否 |
| 207 | `SchoolScene/Exterior/FenceMetal01_1 (1)/FenceMetal01` | MetallPainted01Green | 15 | 0.3132 | 0.1735 | 2.62 | 否 |
| 208 | `SchoolScene/Exterior/FenceMetal01_1 (6)/FenceMetal01` | MetallPainted01Green | 15 | 0.3132 | 0.1735 | 2.62 | 否 |
| 209 | `SchoolScene/Interior/ClassDirector/Notebook01` | Notebook01 | 8 | 0.1579 | 0.1737 | 4.46 | 否 |
| 210 | `SchoolScene/Exterior/FenceMetal01_3/FenceMetal01` | MetallPainted01Green | 13 | 0.2761 | 0.1748 | 2.62 | 否 |
| 211 | `SchoolScene/Exterior/FenceMetal01_3 (3)/FenceMetal01` | MetallPainted01Green | 13 | 0.2761 | 0.1748 | 2.62 | 否 |
| 212 | `SchoolScene/Interior/Toilets/Sink01 (1)` | Sink01 | 10 | 0.1624 | 0.1750 | 4.18 | 否 |
| 213 | `SchoolScene/Interior/ClassDirector/Mousepad02` | Chancellery2 | 0 | 0.2217 | 0.1751 | 4.34 | 否 |
| 214 | `SchoolScene/Interior/ClassDirector/Mousepad03 (2)` | Chancellery3 | 0 | 0.2217 | 0.1751 | 4.30 | 否 |
| 215 | `SchoolScene/Interior/ClassTeachers/Mousepad03` | Chancellery3 | 0 | 0.2217 | 0.1751 | 4.34 | 否 |
| 216 | `SchoolScene/Interior/ClassTeachers/Mousepad03 (1)` | Chancellery3 | 0 | 0.2217 | 0.1751 | 4.34 | 否 |
| 217 | `SchoolScene/Interior/ClassTeachers/Mousepad03 (2)` | Chancellery3 | 0 | 0.2217 | 0.1751 | 4.34 | 否 |
| 218 | `SchoolScene/Interior/ClassTeachers/Mousepad03 (3)` | Chancellery3 | 0 | 0.2217 | 0.1751 | 4.34 | 否 |
| 219 | `SchoolScene/Interior/ClassComputer/Mousepad02` | Chancellery2 | 0 | 0.2217 | 0.1751 | 0.74 | 否 |
| 220 | `SchoolScene/Interior/ClassComputer/Mousepad02 (1)` | Chancellery2 | 0 | 0.2217 | 0.1751 | 0.74 | 否 |
| 221 | `SchoolScene/Interior/ClassComputer/Mousepad02 (2)` | Chancellery2 | 0 | 0.2217 | 0.1751 | 0.74 | 否 |
| 222 | `SchoolScene/Interior/ClassComputer/Mousepad02 (3)` | Chancellery2 | 0 | 0.2217 | 0.1751 | 0.74 | 否 |
| 223 | `SchoolScene/Interior/ClassComputer/TableGroup/Mousepad02 (4)` | Chancellery2 | 0 | 0.2217 | 0.1751 | 0.74 | 否 |
| 224 | `SchoolScene/Interior/ClassComputer/Mousepad02 (5)` | Chancellery2 | 0 | 0.2217 | 0.1751 | 0.74 | 否 |
| 225 | `SchoolScene/Interior/ClassComputer/Mousepad02 (6)` | Chancellery2 | 0 | 0.2217 | 0.1751 | 0.74 | 否 |
| 226 | `SchoolScene/Interior/ClassComputer/Mousepad02 (7)` | Chancellery2 | 0 | 0.2217 | 0.1751 | 0.74 | 否 |
| 227 | `SchoolScene/Interior/ClassComputer/TableGroup (1)/Mousepad02 (4)` | Chancellery2 | 0 | 0.2217 | 0.1751 | 0.74 | 否 |
| 228 | `SchoolScene/Interior/ClassComputer/Mousepad02 (8)` | Chancellery2 | 0 | 0.2217 | 0.1751 | 0.74 | 否 |
| 229 | `SchoolScene/Interior/ClassComputer/Mousepad02 (9)` | Chancellery2 | 0 | 0.2217 | 0.1751 | 0.74 | 否 |
| 230 | `SchoolScene/Interior/ClassComputer/TableGroup (2)/Mousepad02 (4)` | Chancellery2 | 0 | 0.2217 | 0.1751 | 0.74 | 否 |
| 231 | `SchoolScene/Interior/ClassComputer/TableGroup (3)/Mousepad02 (4)` | Chancellery2 | 0 | 0.2217 | 0.1751 | 0.74 | 否 |
| 232 | `SchoolScene/Interior/ClassComputer/Mousepad02 (10)` | Chancellery2 | 0 | 0.2217 | 0.1751 | 0.75 | 否 |
| 233 | `SchoolScene/Interior/ClassComputer/TableGroup (4)/Mousepad02 (4)` | Chancellery2 | 0 | 0.2217 | 0.1751 | 0.74 | 否 |
| 234 | `SchoolScene/Interior/ClassComputer/TableGroup (5)/Mousepad02 (4)` | Chancellery2 | 0 | 0.2217 | 0.1751 | 0.74 | 否 |
| 235 | `SchoolScene/Interior/ClassComputer/TableGroup (6)/Mousepad02 (4)` | Chancellery2 | 0 | 0.2217 | 0.1751 | 0.74 | 否 |
| 236 | `SchoolScene/Interior/ClassComputer/TableGroup (7)/Mousepad02 (4)` | Chancellery2 | 0 | 0.2217 | 0.1751 | 0.74 | 否 |
| 237 | `SchoolScene/Interior/ClassComputer/TableGroup (8)/Mousepad02 (4)` | Chancellery2 | 0 | 0.2217 | 0.1751 | 0.74 | 否 |
| 238 | `SchoolScene/Exterior/FenceMetal01_2 (1)/FenceMetal01` | MetallPainted01Green | 13 | 0.2761 | 0.1757 | 2.62 | 否 |
| 239 | `SchoolScene/Interior/ClassTeachers/Clock1 (1)` | Clock1a | 0 | 0.2217 | 0.1769 | 6.15 | 否 |
| 240 | `SchoolScene/Interior/Class1-2/Clock1b` | Clock1b | 0 | 0.2217 | 0.1769 | 2.52 | 否 |
| 241 | `SchoolScene/Interior/Class1-4/Clock1a (1)` | Clock1a | 0 | 0.2217 | 0.1769 | 2.73 | 否 |
| 242 | `SchoolScene/DayLightsExterior/Light_spot` | Light_spot | 0 | 0.2217 | 0.1769 | 2.78 | **是** |
| 243 | `SchoolScene/DayLightsExterior/Light_spot` | Light_spot | 0 | 0.2217 | 0.1769 | 2.78 | **是** |
| 244 | `SchoolScene/Interior/Class1-3/Chair01b/Chair01Wood` | Wood4 | 8 | 0.1579 | 0.1774 | 0.50 | 否 |
| 245 | `SchoolScene/Interior/Class1-4/FlowerGroup (1)/FlowerHolder01a` | Wood4 \| MetallChrome01 | 3 | 0.2654 | 0.1775 | 1.78 | 否 |
| 246 | `SchoolScene/Interior/Class2-2/TableSchool01 (9)/TableMetall` | MetallPainted01Gray | 8 | 0.1579 | 0.1779 | 3.93 | 否 |
| 247 | `SchoolScene/Interior/Class2-2/TableSchool01 (2)/TableMetall` | MetallPainted01Gray | 8 | 0.1579 | 0.1779 | 3.93 | 否 |
| 248 | `SchoolScene/Interior/Class2-2/TableSchool01 (3)/TableMetall` | MetallPainted01Gray | 8 | 0.1579 | 0.1779 | 3.93 | 否 |
| 249 | `SchoolScene/Interior/Class1-4/TableSchool01 (12)/TableMetall` | MetallPainted01Gray | 8 | 0.1579 | 0.1779 | 0.37 | 否 |
| 250 | `SchoolScene/Interior/Class1-4/TableSchool01 (9)/TableMetall` | MetallPainted01Gray | 8 | 0.1579 | 0.1779 | 0.37 | 否 |
| 251 | `SchoolScene/Interior/Class1-4/TableSchool01 (3)/TableMetall` | MetallPainted01Gray | 8 | 0.1579 | 0.1779 | 0.37 | 否 |
| 252 | `SchoolScene/Exterior/SchoolBuilding/Kitchen` | ConcreteStucco01b \| ConcreteWall6d \| ConcreteWall6g \| TileWhite03 | 21 | 0.2401 | 0.1782 | 1.78 | 否 |
| 253 | `SchoolScene/Exterior/SchoolBuilding/Box001` | ConcreteWall6d \| ConcreteWall6e \| ConcreteWall6g \| ConcreteDirt1b \| ConcreteWall6e \| SportFloor \| ConcreteClear01 | 22 | 0.1443 | 0.1785 | 4.39 | 否 |
| 254 | `SchoolScene/Interior/Corridor2/Poster04_4a (1)` | Wood3 \| PosterBulletin01 \| PosterStickers01 \| ColorChart01 | 11 | 0.2427 | 0.1795 | 5.30 | 否 |
| 255 | `SchoolScene/Interior/Class1-4/Blueprints04` | Geography01b \| Geography02b | 8 | 0.1579 | 0.1805 | 2.16 | 否 |
| 256 | `SchoolScene/Interior/Class1-3/FlowerGroup2 (3)/FlowerRoom4b` | FlowerRoom4 | 10 | 0.1624 | 0.1808 | 1.62 | 否 |
| 257 | `SchoolScene/Interior/SportRoom1/SportInventar01_01 (1)/Balls` | SportBalls | 8 | 0.1579 | 0.1815 | 0.53 | 否 |
| 258 | `SchoolScene/Interior/Class1-3/Electro06 (3)` | Electro06 | 12 | 0.3243 | 0.1815 | 1.70 | 否 |
| 259 | `SchoolScene/Exterior/BuildingBig02b (4)` | ConcreteWall6g \| ConcreteClear01 \| ConcreteClear01 \| BuildingWindows01 \| ConcreteDirt1b \| PlasticLines1Gray | 9 | 0.3047 | 0.1816 | 7.83 | 否 |
| 260 | `SchoolScene/Exterior/SchoolBuilding/Border8` | Wood2 | 9 | 0.3047 | 0.1818 | 0.03 | 否 |
| 261 | `SchoolScene/DayLightsExterior/RoadLamp1a (1)/LampPillar` | ConcretePillar1a | 10 | 0.1624 | 0.1821 | 4.00 | 否 |
| 262 | `SchoolScene/DayLightsExterior/RoadLamp1a (12)/LampPillar` | ConcretePillar1a | 10 | 0.1624 | 0.1821 | 4.00 | 否 |
| 263 | `SchoolScene/Interior/ClassTeachers/Projector01 (1)` | Projector | 10 | 0.1624 | 0.1823 | 6.31 | 否 |
| 264 | `SchoolScene/Interior/ClassComputer/TableGroup (6)/Chair06 (2)/Chair06Metal` | PlasticBlack1 | 10 | 0.1624 | 0.1824 | 0.43 | 否 |
| 265 | `SchoolScene/Interior/ClassComputer/Chair06/Chair06Metal` | PlasticBlack1 | 10 | 0.1624 | 0.1824 | 0.43 | 否 |
| 266 | `SchoolScene/Interior/ClassComputer/Chair06 (10)/Chair06Metal` | PlasticBlack1 | 10 | 0.1624 | 0.1824 | 0.43 | 否 |
| 267 | `SchoolScene/Interior/ClassDirector/Chair01 (2)/Chair01Fabric` | Fabric05c | 0 | 0.2217 | 0.1826 | 4.27 | 否 |
| 268 | `SchoolScene/Interior/ClassDirector/Chair01a/Chair01Fabric` | Fabric05c | 0 | 0.2217 | 0.1826 | 4.27 | 否 |
| 269 | `SchoolScene/Interior/ClassDirector/Chair01a (1)/Chair01Fabric` | Fabric05c | 0 | 0.2217 | 0.1826 | 4.27 | 否 |
| 270 | `SchoolScene/Interior/ClassDirector/Chair01 (8)/Chair01Fabric` | Fabric05c | 0 | 0.2217 | 0.1826 | 4.27 | 否 |
| 271 | `SchoolScene/Interior/ClassDirector/Chair01 (12)/Chair01Fabric` | Fabric05c | 0 | 0.2217 | 0.1826 | 4.27 | 否 |
| 272 | `SchoolScene/Interior/ClassDirector/Chair01 (13)/Chair01Fabric` | Fabric05c | 0 | 0.2217 | 0.1826 | 4.27 | 否 |
| 273 | `SchoolScene/Interior/ClassDirector/Chair01b (1)/Chair01Fabric` | Fabric05a | 0 | 0.2217 | 0.1826 | 4.26 | 否 |
| 274 | `SchoolScene/Interior/Class1-2/Chair01b/Chair01Fabric` | Fabric05a | 0 | 0.2217 | 0.1826 | 0.70 | 否 |
| 275 | `SchoolScene/Exterior/FenceConcrete1_5 (2)` | ConcreteWall2 | 8 | 0.1579 | 0.1832 | 1.00 | 否 |
| 276 | `SchoolScene/DayLightsExterior/RoadLamp2Off/lamp01_1` | MetallPlate03 | 13 | 0.2761 | 0.1835 | 1.09 | 否 |
| 277 | `SchoolScene/DayLightsExterior/RoadLamp2Off/lamp01_1` | MetallPlate03 | 13 | 0.2761 | 0.1835 | 1.09 | 否 |
| 278 | `SchoolScene/Interior/Kitchen/Curtain03_2` | FabricCurtain02b \| MetallChrome01 | 3 | 0.2654 | 0.1837 | 2.63 | 否 |
| 279 | `SchoolScene/Interior/SportClassProps/SportLadder01a (2)` | Wood3 \| MetallGray1 | 8 | 0.1579 | 0.1838 | 1.51 | 否 |
| 280 | `SchoolScene/Interior/SportClassProps/SportLadder01a (3)` | Wood3 \| MetallGray1 | 8 | 0.1579 | 0.1838 | 1.51 | 否 |
| 281 | `SchoolScene/Interior/SportClassProps/Door02Wood1/DoorAnchor/door02_1` | DoorsWindowsPlastic | 7 | 0.3811 | 0.1839 | 1.00 | 否 |
| 282 | `SchoolScene/Exterior/SchoolBuilding/Corridor` | ConcreteWall6d \| ConcreteWall6e \| ConcreteWall6g \| Wood3 \| ConcreteStucco02a \| FloorLinoleum4a \| TileWhite02 \| ConcreteStucco01a \| ConcreteStucco02d \| FloorLinoleum4b \| ConcreteStucco02a \| ConcreteStucco02a \| TileArmstrong01_01 | 4 | 0.2124 | 0.1847 | 1.78 | 否 |
| 283 | `SchoolScene/Interior/SportClassProps/SportBrus1/SprotBrusMetal` | MetallPainted01Green \| MetallChrome01 \| MetallPaintedBlack | 8 | 0.1579 | 0.1850 | 0.65 | 否 |
| 284 | `SchoolScene/Interior/SportClassProps/SportBrus1 (1)/SprotBrusMetal` | MetallPainted01Green \| MetallChrome01 \| MetallPaintedBlack | 8 | 0.1579 | 0.1850 | 0.65 | 否 |
| 285 | `SchoolScene/Interior/Kitchen/FlowerHolder01b (3)` | Wood4 \| MetallChrome01 | 10 | 0.1624 | 0.1866 | 2.12 | 否 |
| 286 | `SchoolScene/Interior/Class1-2/TableSchool03 (1)/TableChrome` | MetallChrome01 | 0 | 0.2217 | 0.1867 | 0.52 | 否 |
| 287 | `SchoolScene/Interior/Class1-2/TableSchool03 (2)/TableChrome` | MetallChrome01 | 0 | 0.2217 | 0.1867 | 0.52 | 否 |
| 288 | `SchoolScene/Interior/Class1-2/TableSchool03 (3)/TableChrome` | MetallChrome01 | 0 | 0.2217 | 0.1867 | 0.52 | 否 |
| 289 | `SchoolScene/Interior/Class1-2/TableSchool03 (4)/TableChrome` | MetallChrome01 | 0 | 0.2217 | 0.1867 | 0.52 | 否 |
| 290 | `SchoolScene/Interior/Class1-2/TableSchool03 (5)/TableChrome` | MetallChrome01 | 0 | 0.2217 | 0.1867 | 0.52 | 否 |
| 291 | `SchoolScene/Interior/Class1-2/TableSchool03 (6)/TableChrome` | MetallChrome01 | 0 | 0.2217 | 0.1867 | 0.52 | 否 |
| 292 | `SchoolScene/Interior/Class1-2/TableSchool03 (7)/TableChrome` | MetallChrome01 | 0 | 0.2217 | 0.1867 | 0.52 | 否 |
| 293 | `SchoolScene/Interior/Corridor1/Poster04_1a (1)` | Wood3 \| Cork01b \| PosterStickers01 \| ColorChart01 | 12 | 0.3243 | 0.1868 | 1.69 | 否 |
| 294 | `SchoolScene/Interior/Kitchen/SchoolKitchenSeats (19)/TableSimple01_02/TableMetall` | MetallPainted01Gray | 3 | 0.2654 | 0.1868 | 0.40 | 否 |
| 295 | `SchoolScene/Interior/Kitchen/SchoolKitchenSeats (18)/TableSimple01_02/TableMetall` | MetallPainted01Gray | 3 | 0.2654 | 0.1868 | 0.40 | 否 |
| 296 | `SchoolScene/Interior/Kitchen/SchoolKitchenSeats (17)/TableSimple01_02/TableMetall` | MetallPainted01Gray | 3 | 0.2654 | 0.1868 | 0.40 | 否 |
| 297 | `SchoolScene/Interior/Kitchen/SchoolKitchenSeats (12)/TableSimple01_02/TableMetall` | MetallPainted01Gray | 3 | 0.2654 | 0.1868 | 0.40 | 否 |
| 298 | `SchoolScene/Interior/Kitchen/SchoolKitchenSeats (11)/TableSimple01_02/TableMetall` | MetallPainted01Gray | 3 | 0.2654 | 0.1868 | 0.40 | 否 |
| 299 | `SchoolScene/Interior/Kitchen/SchoolKitchenSeats (10)/TableSimple01_02/TableMetall` | MetallPainted01Gray | 3 | 0.2654 | 0.1868 | 0.40 | 否 |
| 300 | `SchoolScene/Exterior/BuildingKPP2/Budka1` | ModelBuildingKPP2 | 11 | 0.2427 | 0.1871 | 1.04 | 否 |
| 301 | `SchoolScene/Interior/Class2-2/Board01_2a` | PlasticWhite1 \| Board01b | 13 | 0.2761 | 0.1872 | 5.08 | 否 |
| 302 | `SchoolScene/Interior/Class1-3/ElectroWires07_2` | ElectroWires01 | 0 | 0.2217 | 0.1874 | 0.91 | 否 |
| 303 | `SchoolScene/Interior/Corridor1/Poster04_4a` | Wood3 \| PosterBulletin01 \| PosterStickers01 \| ColorChart01 | 10 | 0.1624 | 0.1875 | 1.71 | 否 |
| 304 | `SchoolScene/Interior/SportRoom3/Bath01 (1)/BathsWood` | PlasticWhite1 | 13 | 0.2761 | 0.1876 | 1.10 | 否 |
| 305 | `SchoolScene/Exterior/FenceConcrete1_1` | ConcreteWall2 | 0 | 0.2217 | 0.1879 | 1.00 | 否 |
| 306 | `SchoolScene/Interior/SportClassProps/SportBasketShit01 (3)/BasketMetal` | MetallPainted01Red \| MetallPainted01Gray | 0 | 0.2217 | 0.1885 | 3.26 | 否 |
| 307 | `SchoolScene/Interior/SportClassProps/SportBasketShit01/BasketMetal` | MetallPainted01Red \| MetallPainted01Gray | 0 | 0.2217 | 0.1885 | 3.26 | 否 |
| 308 | `SchoolScene/DayLightsExterior/RoadLamp1a (3)/LampPillar` | ConcretePillar1a | 8 | 0.1579 | 0.1887 | 4.00 | 否 |
| 309 | `SchoolScene/DayLightsExterior/RoadLamp1a (10)/LampPillar` | ConcretePillar1a | 8 | 0.1579 | 0.1887 | 4.00 | 否 |
| 310 | `SchoolScene/DayLightsExterior/RoadLamp1a (14)/LampPillar` | ConcretePillar1a | 8 | 0.1579 | 0.1887 | 4.00 | 否 |
| 311 | `SchoolScene/Interior/Doors/DoorDouble02/DoorDouble` | DoorDouble02 \| DoorDoubleparts02 | 3 | 0.2654 | 0.1889 | 4.57 | 否 |
| 312 | `SchoolScene/Interior/SportClassProps/SportMattress01 (1)` | Leather06Blue | 8 | 0.1579 | 0.1889 | 0.09 | 否 |
| 313 | `SchoolScene/Interior/SportRoom1/SportMattress01 (3)` | Leather06Blue | 8 | 0.1579 | 0.1889 | 0.74 | 否 |
| 314 | `SchoolScene/Interior/SportRoom1/SportMattress01 (4)` | Leather06Blue | 8 | 0.1579 | 0.1889 | 0.74 | 否 |
| 315 | `SchoolScene/Interior/ClassComputer/Monitor04` | Monitor04 | 10 | 0.1624 | 0.1891 | 0.99 | 否 |
| 316 | `SchoolScene/Interior/ClassComputer/Monitor04 (5)` | Monitor04 | 10 | 0.1624 | 0.1891 | 0.99 | 否 |
| 317 | `SchoolScene/Interior/ClassComputer/Monitor04 (6)` | Monitor04 | 10 | 0.1624 | 0.1891 | 0.99 | 否 |
| 318 | `SchoolScene/Interior/ClassComputer/TableGroup (6)/Monitor04 (4)` | Monitor04 | 10 | 0.1624 | 0.1891 | 0.99 | 否 |
| 319 | `SchoolScene/Interior/ClassComputer/TableGroup (4)/Computer06` | Computer06 | 11 | 0.2427 | 0.1902 | 0.31 | 否 |
| 320 | `SchoolScene/Interior/ClassComputer/Computer06 (10)` | Computer06 | 11 | 0.2427 | 0.1902 | 0.31 | 否 |
| 321 | `SchoolScene/Exterior/BordersArea (1)/RoadBordure1e` | ConcreteWall5 | 8 | 0.1579 | 0.1903 | -0.40 | 否 |
| 322 | `SchoolScene/Exterior/BordersArea (4)/RoadBordure1e (1)` | ConcreteWall5 | 8 | 0.1579 | 0.1903 | -0.40 | 否 |
| 323 | `SchoolScene/Exterior/RoadBordure1e (3)` | ConcreteWall5 | 8 | 0.1579 | 0.1903 | -0.40 | 否 |
| 324 | `SchoolScene/Interior/Class1-2/FlowerRoom4a (1)` | FlowerRoom4 | 0 | 0.2217 | 0.1910 | 2.13 | 否 |
| 325 | `SchoolScene/Interior/Class1-3/FlowerGroup (3)/FlowerRoom4a` | FlowerRoom4 | 0 | 0.2217 | 0.1910 | 2.20 | 否 |
| 326 | `SchoolScene/Interior/ClassDirector/Clock2a` | Clock2a | 0 | 0.2217 | 0.1912 | 5.93 | 否 |
| 327 | `SchoolScene/Interior/Class1-3/Clock2a` | Clock2a | 0 | 0.2217 | 0.1912 | 2.52 | 否 |
| 328 | `SchoolScene/Interior/ClassComputer/Clock2b` | Clock2b | 0 | 0.2217 | 0.1912 | 2.42 | 否 |
| 329 | `SchoolScene/Exterior/ConcreteBox01 (36)` | ConcreteClear03 | 3 | 0.2654 | 0.1917 | -0.18 | 否 |
| 330 | `SchoolScene/Interior/SportClassProps/Door02_OpenedWood1/door02_frame` | DoorWindowsWood1 | 8 | 0.1579 | 0.1923 | 1.02 | 否 |
| 331 | `SchoolScene/Interior/Kitchen/KitchenRazdacha01_01a (1)` | MetallChrome03 \| PlasticRed1 \| PlasticBlack1 | 11 | 0.2427 | 0.1924 | 0.46 | 否 |
| 332 | `SchoolScene/Interior/Toilets/ToiletCabinet01/ToiletDoor2` | ToiletCabinet01 | 13 | 0.2761 | 0.1929 | 4.72 | 否 |
| 333 | `SchoolScene/Interior/Toilets/ToiletCabinet01/ToiletDoor4` | ToiletCabinet01 | 13 | 0.2761 | 0.1929 | 4.72 | 否 |
| 334 | `SchoolScene/Interior/ClassDirector/Capboard01_05 (1)/GlassPart1` | GlassClear | 0 | 0.2217 | 0.1941 | 4.97 | 否 |
| 335 | `SchoolScene/Interior/Class1-2/Capboard01_05 (1)/GlassPart1` | GlassClear | 0 | 0.2217 | 0.1941 | 1.41 | 否 |
| 336 | `SchoolScene/Interior/ActZal/Seats01 (7)/Cylinder004` | MetallChrome01 | 0 | 0.2217 | 0.1944 | 4.05 | 否 |
| 337 | `SchoolScene/Interior/ActZal/Seats01 (4)/Cylinder004` | MetallChrome01 | 0 | 0.2217 | 0.1944 | 4.05 | 否 |
| 338 | `SchoolScene/Interior/ActZal/Seats01/Cylinder004` | MetallChrome01 | 0 | 0.2217 | 0.1944 | 4.05 | 否 |
| 339 | `SchoolScene/Interior/ActZal/Seats01 (3)/Cylinder004` | MetallChrome01 | 0 | 0.2217 | 0.1944 | 4.05 | 否 |
| 340 | `SchoolScene/Interior/Toilets/Sink01` | Sink01 | 13 | 0.2761 | 0.1945 | 4.18 | 否 |
| 341 | `SchoolScene/Interior/Class1-2/ChemistryProp05 (3)` | PlasticWhite1 \| GlassClear | 0 | 0.2217 | 0.1949 | 1.64 | 否 |
| 342 | `SchoolScene/Interior/Class1-2/ChemistryProp05 (2)` | PlasticWhite1 \| GlassClear | 0 | 0.2217 | 0.1949 | 1.64 | 否 |
| 343 | `SchoolScene/Interior/Class1-2/ChemistryProp05` | PlasticWhite1 \| GlassClear | 0 | 0.2217 | 0.1949 | 1.21 | 否 |
| 344 | `SchoolScene/Interior/Class1-2/ChemistryProp05 (1)` | PlasticWhite1 \| GlassClear | 0 | 0.2217 | 0.1949 | 1.21 | 否 |
| 345 | `SchoolScene/Interior/Class1-4/FlowerGroup2 (1)/FlowerHolder01b` | Wood4 \| MetallChrome01 | 8 | 0.1579 | 0.1957 | 1.87 | 否 |
| 346 | `SchoolScene/Interior/SportRoom1/Sportkozel01 (1)/KozelLeather` | Leather06 | 10 | 0.1624 | 0.1963 | 1.00 | 否 |
| 347 | `SchoolScene/Interior/Kitchen/KitchenRazdacha01_03a/Glass` | GlassClear | 0 | 0.2217 | 0.1971 | 1.37 | 否 |
| 348 | `SchoolScene/Interior/Kitchen/KitchenFurniture02` | MetallChrome03 \| PlasticBlack1 | 5 | 0.3190 | 0.1973 | 1.87 | 否 |
| 349 | `SchoolScene/Interior/Kitchen/KitchenFurniture02 (1)` | MetallChrome03 \| PlasticBlack1 | 5 | 0.3190 | 0.1973 | 1.87 | 否 |
| 350 | `SchoolScene/DayLightsInterior/LightLum01_01 (6)` | LightLum01 | 0 | 0.2217 | 0.1980 | 3.27 | 否 |
| 351 | `SchoolScene/DayLightsInterior/LightLum01_01 (10)` | LightLum01 | 0 | 0.2217 | 0.1980 | 3.28 | 否 |
| 352 | `SchoolScene/DayLightsInterior/LightLum01_01 (4)` | LightLum01 | 0 | 0.2217 | 0.1980 | 6.83 | 否 |
| 353 | `SchoolScene/DayLightsInterior/LightLum01_01 (5)` | LightLum01 | 0 | 0.2217 | 0.1980 | 6.83 | 否 |
| 354 | `SchoolScene/DayLightsInterior/LightLum01_01 (1)` | LightLum01 | 0 | 0.2217 | 0.1980 | 3.27 | 否 |
| 355 | `SchoolScene/DayLightsInterior/LightLum01_01 (3)` | LightLum01 | 0 | 0.2217 | 0.1980 | 3.27 | 否 |
| 356 | `SchoolScene/DayLightsInterior/LightLum01_01 (5)` | LightLum01 | 0 | 0.2217 | 0.1980 | 3.27 | 否 |
| 357 | `SchoolScene/DayLightsInterior/LightLum01_01 (6)` | LightLum01 | 0 | 0.2217 | 0.1980 | 6.83 | 否 |
| 358 | `SchoolScene/DayLightsInterior/LightLum01_01 (10)` | LightLum01 | 0 | 0.2217 | 0.1980 | 6.84 | 否 |
| 359 | `SchoolScene/DayLightsInterior/LightLum01_01 (13)` | LightLum01 | 0 | 0.2217 | 0.1980 | 3.28 | 否 |
| 360 | `SchoolScene/DayLightsInterior/LightLum01_01` | LightLum01 | 0 | 0.2217 | 0.1980 | 3.30 | 否 |
| 361 | `SchoolScene/DayLightsInterior/LightLum01_01 (33)` | LightLum01 | 0 | 0.2217 | 0.1980 | 3.16 | 否 |
| 362 | `SchoolScene/Interior/SportClassProps/SportLadder01b (4)` | Wood3 \| MetallGray1 | 8 | 0.1579 | 0.1983 | 1.51 | 否 |
| 363 | `SchoolScene/Interior/SportClassProps/SportLadder01b (5)` | Wood3 \| MetallGray1 | 8 | 0.1579 | 0.1983 | 1.51 | 否 |
| 364 | `SchoolScene/Interior/SportClassProps/SportLadder01b (6)` | Wood3 \| MetallGray1 | 8 | 0.1579 | 0.1983 | 1.51 | 否 |
| 365 | `SchoolScene/Interior/Toilets/ToiletCabinet01/ToiletDoor1` | ToiletCabinet01 | 12 | 0.3243 | 0.1984 | 4.72 | 否 |
| 366 | `SchoolScene/Interior/ClassDirector/TableOffice1/22.bmp` | TableOffice1kr | 0 | 0.2217 | 0.1986 | 4.13 | 否 |
| 367 | `SchoolScene/Interior/SportClassProps/Radiator01 (7)` | Radiator01 | 13 | 0.2761 | 0.1990 | 0.39 | 否 |
| 368 | `SchoolScene/Interior/SportClassProps/Radiator01 (2)` | Radiator01 | 13 | 0.2761 | 0.1990 | 0.38 | 否 |
| 369 | `SchoolScene/Interior/Class2-1/Radiator01 (4)` | Radiator01 | 13 | 0.2761 | 0.1990 | 3.86 | 否 |
| 370 | `SchoolScene/Interior/ClassComputer/Radiator01 (7)` | Radiator01 | 13 | 0.2761 | 0.1990 | 0.28 | 否 |
| 371 | `SchoolScene/Exterior/FenceMetal01_4 (1)/FenceMetal01` | MetallPainted01Green | 0 | 0.2217 | 0.1990 | 2.56 | 否 |
| 372 | `SchoolScene/Interior/ActZal/Seats01 (21)/Cylinder004` | MetallChrome01 | 8 | 0.1579 | 0.1993 | 4.05 | 否 |
| 373 | `SchoolScene/Interior/ActZal/Seats01 (17)/Cylinder004` | MetallChrome01 | 8 | 0.1579 | 0.1993 | 4.05 | 否 |
| 374 | `SchoolScene/Interior/SportClassProps/Door02Wood1/DoorAnchor/door02_1` | DoorWindowsWood1 | 8 | 0.1579 | 0.1996 | 1.01 | 否 |
| 375 | `SchoolScene/Interior/Toilets/ToiletCabinet01/ToiletCabinet01` | ToiletCabinet01 | 13 | 0.2761 | 0.1998 | 4.80 | 否 |
| 376 | `SchoolScene/Interior/Corridor1/Poster03_2a (2)/Poster03_2` | MetallChrome01 \| PosterInfo01 \| MetallChrome01 | 3 | 0.2654 | 0.2003 | 5.27 | 否 |
| 377 | `SchoolScene/DayLightsInterior/LightLum01_01 (2)` | LightLum01 | 7 | 0.3811 | 0.2003 | 3.28 | 否 |
| 378 | `SchoolScene/DayLightsInterior/LightLum01_01 (5)` | LightLum01 | 7 | 0.3811 | 0.2003 | 3.28 | 否 |
| 379 | `SchoolScene/Interior/ClassTeachers/Board01_3a` | PlasticWhite1 \| PlasticBlack1 \| PlasticWhite1 | 13 | 0.2761 | 0.2010 | 4.98 | 否 |
| 380 | `SchoolScene/Interior/ClassDirector/Chair01 (4)/Chair01Wood` | Wood4 | 13 | 0.2761 | 0.2020 | 4.06 | 否 |
| 381 | `SchoolScene/Interior/ClassDirector/Chair01 (5)/Chair01Wood` | Wood4 | 13 | 0.2761 | 0.2020 | 4.06 | 否 |
| 382 | `SchoolScene/Interior/Class1-3/Electro05_4 (1)` | Electro05 | 0 | 0.2217 | 0.2028 | 0.78 | 否 |
| 383 | `SchoolScene/Interior/Class1-3/Electro05_4` | Electro05 | 0 | 0.2217 | 0.2028 | 0.88 | 否 |
| 384 | `SchoolScene/Interior/Class1-3/ElectroWires07_1` | ElectroWires01 | 0 | 0.2217 | 0.2028 | 0.91 | 否 |
| 385 | `SchoolScene/Interior/ClassDirector/Capboard01_02/Capboard01` | Wood4 | 0 | 0.2217 | 0.2031 | 4.60 | 否 |
| 386 | `SchoolScene/Interior/ClassComputer/Blinds01c (8)` | BlindsEmmisive \| Default-Material \| Default-Material | 10 | 0.1624 | 0.2034 | 2.04 | 否 |
| 387 | `SchoolScene/Interior/Class1-2/ChemistryProp06` | GlassClear | 0 | 0.2217 | 0.2037 | 1.19 | 否 |
| 388 | `SchoolScene/Interior/Class1-2/ChemistryProp06 (3)` | GlassClear | 0 | 0.2217 | 0.2037 | 1.62 | 否 |
| 389 | `SchoolScene/Interior/Class1-2/ChemistryProp06 (2)` | GlassClear | 0 | 0.2217 | 0.2037 | 1.62 | 否 |
| 390 | `SchoolScene/Interior/Class1-2/ChemistryProp06 (1)` | GlassClear | 0 | 0.2217 | 0.2037 | 1.19 | 否 |
| 391 | `SchoolScene/Interior/Kitchen/TableSimple01_01 (1)/TableWood` | Wood3 | 16 | 0.3336 | 0.2043 | 0.79 | 否 |
| 392 | `SchoolScene/Exterior/SchoolBuilding/Border9` | Wood2 | 10 | 0.1624 | 0.2043 | 0.03 | 否 |
| 393 | `SchoolScene/DayLightsExterior/RoadLamp2Off/LampGlass` | GlassWhite | 0 | 0.2217 | 0.2044 | 2.83 | 否 |
| 394 | `SchoolScene/DayLightsExterior/RoadLamp2Off/LampGlass` | GlassWhite | 0 | 0.2217 | 0.2044 | 2.83 | 否 |
| 395 | `SchoolScene/DayLightsExterior/RoadLamp2Off/LampGlass` | GlassWhite | 0 | 0.2217 | 0.2044 | 2.83 | 否 |
| 396 | `SchoolScene/DayLightsExterior/RoadLamp2Off/LampGlass` | GlassWhite | 0 | 0.2217 | 0.2044 | 2.83 | 否 |
| 397 | `SchoolScene/DayLightsExterior/RoadLamp2Off/LampGlass` | GlassWhite | 0 | 0.2217 | 0.2044 | 2.83 | 否 |
| 398 | `SchoolScene/DayLightsExterior/RoadLamp2Off/LampGlass` | GlassWhite | 0 | 0.2217 | 0.2044 | 2.83 | 否 |
| 399 | `SchoolScene/DayLightsExterior/RoadLamp2Off/LampGlass` | GlassWhite | 0 | 0.2217 | 0.2044 | 2.83 | 否 |
| 400 | `SchoolScene/DayLightsExterior/RoadLamp2Off/LampGlass` | GlassWhite | 0 | 0.2217 | 0.2044 | 2.83 | 否 |
| 401 | `SchoolScene/DayLightsExterior/RoadLamp2Off/LampGlass` | GlassWhite | 0 | 0.2217 | 0.2044 | 2.83 | 否 |
| 402 | `SchoolScene/DayLightsExterior/RoadLamp2Off/LampGlass` | GlassWhite | 0 | 0.2217 | 0.2044 | 2.83 | 否 |
| 403 | `SchoolScene/DayLightsExterior/RoadLamp2Off/LampGlass` | GlassWhite | 0 | 0.2217 | 0.2044 | 2.83 | 否 |
| 404 | `SchoolScene/DayLightsExterior/RoadLamp2Off/LampGlass` | GlassWhite | 0 | 0.2217 | 0.2044 | 2.83 | 否 |
| 405 | `SchoolScene/DayLightsExterior/RoadLamp2Off/LampGlass` | GlassWhite | 0 | 0.2217 | 0.2044 | 2.83 | 否 |
| 406 | `SchoolScene/DayLightsExterior/RoadLamp2Off/LampGlass` | GlassWhite | 0 | 0.2217 | 0.2044 | 2.83 | 否 |
| 407 | `SchoolScene/DayLightsExterior/RoadLamp2Off/LampGlass` | GlassWhite | 0 | 0.2217 | 0.2044 | 2.83 | 否 |
| 408 | `SchoolScene/Interior/Class1-2/ChemistryMol01` | ColorChart01 | 0 | 0.2217 | 0.2047 | 0.81 | 否 |
| 409 | `SchoolScene/Interior/Class1-3/Poster01_1a` | MetallChrome01 \| Physics02 | 13 | 0.2761 | 0.2050 | 1.68 | 否 |
| 410 | `SchoolScene/Interior/SportRoom1/SportInventar01_02/Wood` | Wood5 | 10 | 0.1624 | 0.2051 | 0.67 | 否 |
| 411 | `SchoolScene/Interior/SportRoom1/SportInventar01_01/Wood` | Wood5 | 10 | 0.1624 | 0.2051 | 0.67 | 否 |
| 412 | `SchoolScene/Interior/Class1-2/ChemistryProp03 (5)` | MetallChrome01 \| GlassClear | 0 | 0.2217 | 0.2053 | 1.62 | 否 |
| 413 | `SchoolScene/Interior/Class1-2/ChemistryProp03 (4)` | MetallChrome01 \| GlassClear | 0 | 0.2217 | 0.2053 | 1.62 | 否 |
| 414 | `SchoolScene/Interior/Class1-2/ChemistryProp03 (3)` | MetallChrome01 \| GlassClear | 0 | 0.2217 | 0.2053 | 1.62 | 否 |
| 415 | `SchoolScene/Interior/Class1-2/ChemistryProp03 (2)` | MetallChrome01 \| GlassClear | 0 | 0.2217 | 0.2053 | 1.19 | 否 |
| 416 | `SchoolScene/Interior/Class1-2/ChemistryProp03 (1)` | MetallChrome01 \| GlassClear | 0 | 0.2217 | 0.2053 | 1.19 | 否 |
| 417 | `SchoolScene/Interior/Class1-2/ChemistryProp03` | MetallChrome01 \| GlassClear | 0 | 0.2217 | 0.2053 | 0.86 | 否 |
| 418 | `SchoolScene/Exterior/BordersArea/RoadBordure1a (1)` | ConcreteWall5 | 5 | 0.3190 | 0.2060 | -0.40 | 否 |
| 419 | `SchoolScene/Interior/Kitchen/KitchenRazdacha01_04a` | MetallChrome03 \| PlasticRed1 \| PlasticBlack1 | 12 | 0.3243 | 0.2069 | 0.40 | 否 |
| 420 | `SchoolScene/Exterior/BordersArea/RoadBordure1b` | ConcreteWall5 | 5 | 0.3190 | 0.2080 | -0.40 | 否 |
| 421 | `SchoolScene/Exterior/BordersArea/RoadBordure1b (10)` | ConcreteWall5 | 5 | 0.3190 | 0.2080 | -0.40 | 否 |
| 422 | `SchoolScene/Exterior/BordersArea/RoadBordure1b (24)` | ConcreteWall5 | 5 | 0.3190 | 0.2080 | -0.40 | 否 |
| 423 | `SchoolScene/Exterior/BordersArea (2)/RoadBordure1b` | ConcreteWall5 | 5 | 0.3190 | 0.2080 | -0.40 | 否 |
| 424 | `SchoolScene/Interior/Toilets/Toilet01Brush` | Toilet01 | 0 | 0.2217 | 0.2081 | 3.84 | 否 |
| 425 | `SchoolScene/Interior/Toilets/Toilet01Brush (1)` | Toilet01 | 0 | 0.2217 | 0.2081 | 3.84 | 否 |
| 426 | `SchoolScene/Interior/Toilets/Toilet01Brush (2)` | Toilet01 | 0 | 0.2217 | 0.2081 | 3.84 | 否 |
| 427 | `SchoolScene/Interior/Toilets/Toilet01Brush (3)` | Toilet01 | 0 | 0.2217 | 0.2081 | 3.84 | 否 |
| 428 | `SchoolScene/Interior/Kitchen/KitchenFurniture04_2` | MetallChrome03 \| PlasticBlack1 \| GlassMirrorBlack | 5 | 0.3190 | 0.2082 | 0.47 | 否 |
| 429 | `SchoolScene/Interior/Class1-2/TableSchool04 (1)/TableMetal` | MetallChrome01 | 0 | 0.2217 | 0.2084 | 1.15 | 否 |
| 430 | `SchoolScene/Interior/Class1-2/TableSchool04 (2)/TableMetal` | MetallChrome01 | 0 | 0.2217 | 0.2084 | 1.15 | 否 |
| 431 | `SchoolScene/Interior/Class1-3/Electro06 (1)` | Electro06 | 0 | 0.2217 | 0.2084 | 0.85 | 否 |
| 432 | `SchoolScene/Interior/Corridor1/Poster04_2a` | Wood3 \| Cork01b \| PosterStickers01 \| ColorChart01 | 0 | 0.2217 | 0.2103 | 1.69 | 否 |
| 433 | `SchoolScene/Interior/SportRoom1/SportTrampline01 (2)` | Wood3 \| Leather07 | 10 | 0.1624 | 0.2118 | 0.12 | 否 |
| 434 | `SchoolScene/DayLightsExterior/RoadLamp1a (3)/LampMetall` | MetallChrome01 | 8 | 0.1579 | 0.2122 | 9.15 | 否 |
| 435 | `SchoolScene/DayLightsExterior/RoadLamp1a (10)/LampMetall` | MetallChrome01 | 8 | 0.1579 | 0.2122 | 9.15 | 否 |
| 436 | `SchoolScene/DayLightsExterior/RoadLamp1a (14)/LampMetall` | MetallChrome01 | 8 | 0.1579 | 0.2122 | 9.15 | 否 |
| 437 | `SchoolScene/Interior/ClassComputer/Projector01` | Projector | 11 | 0.2427 | 0.2124 | 2.85 | 否 |
| 438 | `SchoolScene/Interior/SportClassProps/SportBench01` | MetallPaintedBlack \| Wood5 | 13 | 0.2761 | 0.2124 | 0.16 | 否 |
| 439 | `SchoolScene/Interior/SportClassProps/SportBench01 (3)` | MetallPaintedBlack \| Wood5 | 13 | 0.2761 | 0.2124 | 0.16 | 否 |
| 440 | `SchoolScene/Exterior/BordersArea/RoadBordure1c` | ConcreteWall5 | 8 | 0.1579 | 0.2124 | -0.40 | 否 |
| 441 | `SchoolScene/Exterior/BordersArea/RoadBordure1c (7)` | ConcreteWall5 | 8 | 0.1579 | 0.2124 | -0.40 | 否 |
| 442 | `SchoolScene/Exterior/BordersArea (4)/RoadBordure1c (1)` | ConcreteWall5 | 8 | 0.1579 | 0.2124 | -0.40 | 否 |
| 443 | `SchoolScene/Exterior/BordersArea (4)/RoadBordure1c (2)` | ConcreteWall5 | 8 | 0.1579 | 0.2124 | -0.40 | 否 |
| 444 | `SchoolScene/Interior/ClassDirector/TableOffice1/ctolbik4.bmp` | TableOffice1st | 0 | 0.2217 | 0.2126 | 4.20 | 否 |
| 445 | `SchoolScene/Interior/ClassDirector/Capboard01_01/CapboardChrome` | MetallChrome01 | 0 | 0.2217 | 0.2138 | 4.18 | 否 |
| 446 | `SchoolScene/Interior/ClassDirector/Capboard01_02/CapboardChrome` | MetallChrome01 | 0 | 0.2217 | 0.2138 | 4.18 | 否 |
| 447 | `SchoolScene/Interior/ClassDirector/Capboard01_01 (1)/CapboardChrome` | MetallChrome01 | 0 | 0.2217 | 0.2138 | 4.18 | 否 |
| 448 | `SchoolScene/Interior/Class1-3/Capboard01_02/CapboardChrome` | MetallChrome01 | 0 | 0.2217 | 0.2138 | 0.62 | 否 |
| 449 | `SchoolScene/Interior/Class1-3/Capboard01_01/CapboardChrome` | MetallChrome01 | 0 | 0.2217 | 0.2138 | 0.62 | 否 |
| 450 | `SchoolScene/Interior/Class1-3/Capboard01_01 (1)/CapboardChrome` | MetallChrome01 | 0 | 0.2217 | 0.2138 | 0.62 | 否 |
| 451 | `SchoolScene/Interior/Class1-3/Capboard01_02 (1)/CapboardChrome` | MetallChrome01 | 0 | 0.2217 | 0.2138 | 0.62 | 否 |
| 452 | `SchoolScene/Interior/Class1-4/Capboard01_01/CapboardChrome` | MetallChrome01 | 0 | 0.2217 | 0.2138 | 0.62 | 否 |
| 453 | `SchoolScene/Interior/Class1-4/Capboard01_01 (2)/CapboardChrome` | MetallChrome01 | 0 | 0.2217 | 0.2138 | 0.62 | 否 |
| 454 | `SchoolScene/Interior/ClassDirector/Capboard01_05 (2)/GlassPart2` | GlassClear | 0 | 0.2217 | 0.2138 | 4.97 | 否 |
| 455 | `SchoolScene/Interior/Class1-2/Capboard01_05 (1)/GlassPart2` | GlassClear | 0 | 0.2217 | 0.2138 | 1.41 | 否 |
| 456 | `SchoolScene/Interior/Doors/Door07a (3)` | Door07a \| Doorparts01 | 13 | 0.2761 | 0.2139 | 1.01 | 否 |
| 457 | `SchoolScene/Interior/Doors/Door02` | Door02 \| Doorparts04 | 13 | 0.2761 | 0.2139 | 4.57 | 否 |
| 458 | `SchoolScene/Interior/Doors/Door07a (8)` | Door07a \| Doorparts01 | 13 | 0.2761 | 0.2139 | 4.56 | 否 |
| 459 | `SchoolScene/DayLightsInterior/LightLum01_01 (2)` | LightLum01 | 5 | 0.3190 | 0.2140 | 6.84 | 否 |
| 460 | `SchoolScene/Interior/Corridor1/Poster04_6a` | Wood3 \| PosterBulletin01 \| PosterStickers01 \| ColorChart01 | 9 | 0.3047 | 0.2142 | 1.71 | 否 |
| 461 | `SchoolScene/Interior/Class1-3/Electro08_7 (1)` | Electro08 | 0 | 0.2217 | 0.2143 | 1.18 | 否 |
| 462 | `SchoolScene/Interior/Class1-3/Electro08_7` | Electro08 | 0 | 0.2217 | 0.2143 | 0.87 | 否 |
| 463 | `TreeShadows/Tree2(Clone)` | Optimized Bark Material \| Optimized Leaf Material | 11 | 0.2427 | 0.2144 | 6.34 | 否 |
| 464 | `SchoolScene/Interior/SportClassProps/SportBasketShit01 (1)/Basket` | MetallPainted01White | 0 | 0.2217 | 0.2153 | 2.77 | 否 |
| 465 | `SchoolScene/Interior/SportClassProps/SportBasketShit01 (2)/Basket` | MetallPainted01White | 0 | 0.2217 | 0.2153 | 2.77 | 否 |
| 466 | `SchoolScene/Interior/SportClassProps/SportBasketShit01 (3)/Basket` | MetallPainted01White | 0 | 0.2217 | 0.2153 | 2.77 | 否 |
| 467 | `SchoolScene/Interior/SportClassProps/SportBasketShit01 (4)/Basket` | MetallPainted01White | 0 | 0.2217 | 0.2153 | 3.04 | 否 |
| 468 | `SchoolScene/Interior/SportClassProps/SportBasketShit01 (5)/Basket` | MetallPainted01White | 0 | 0.2217 | 0.2153 | 3.04 | 否 |
| 469 | `SchoolScene/Interior/SportClassProps/SportBasketShit01/Basket` | MetallPainted01White | 0 | 0.2217 | 0.2153 | 2.77 | 否 |
| 470 | `SchoolScene/Exterior/Benches/Bench01_1 (6)/Wood` | Wood2 | 0 | 0.2217 | 0.2153 | -0.08 | 否 |
| 471 | `SchoolScene/Exterior/Benches/Bench01_1 (7)/Wood` | Wood2 | 0 | 0.2217 | 0.2153 | -0.08 | 否 |
| 472 | `SchoolScene/Exterior/Benches/Bench01_1 (10)/Wood` | Wood2 | 0 | 0.2217 | 0.2153 | -0.08 | 否 |
| 473 | `SchoolScene/Exterior/Benches/Bench01_1 (11)/Wood` | Wood2 | 0 | 0.2217 | 0.2153 | -0.08 | 否 |
| 474 | `SchoolScene/Interior/SportClassProps/SportGates01b/Gates002` | SportGates | 0 | 0.2217 | 0.2154 | 0.98 | 否 |
| 475 | `SchoolScene/Interior/SportClassProps/SportGates02 (3)/Gates002` | SportGates | 0 | 0.2217 | 0.2154 | 0.98 | 否 |
| 476 | `SchoolScene/Interior/ClassDirector/Chair01 (2)/Chair01Wood` | WoodOak | 0 | 0.2217 | 0.2161 | 4.06 | 否 |
| 477 | `SchoolScene/Interior/ClassDirector/Chair01a/Chair01Wood` | WoodOak | 0 | 0.2217 | 0.2161 | 4.06 | 否 |
| 478 | `SchoolScene/Interior/ClassDirector/Chair01a (1)/Chair01Wood` | WoodOak | 0 | 0.2217 | 0.2161 | 4.06 | 否 |
| 479 | `SchoolScene/Interior/ClassDirector/Chair01 (8)/Chair01Wood` | WoodOak | 0 | 0.2217 | 0.2161 | 4.06 | 否 |
| 480 | `SchoolScene/Interior/ClassDirector/Chair01 (12)/Chair01Wood` | WoodOak | 0 | 0.2217 | 0.2161 | 4.06 | 否 |
| 481 | `SchoolScene/Interior/ClassDirector/Chair01 (13)/Chair01Wood` | WoodOak | 0 | 0.2217 | 0.2161 | 4.06 | 否 |
| 482 | `SchoolScene/Interior/ClassDirector/Chair01b (1)/Chair01Wood` | Wood4 | 0 | 0.2217 | 0.2161 | 4.06 | 否 |
| 483 | `SchoolScene/Interior/Class1-2/Chair01b/Chair01Wood` | Wood4 | 0 | 0.2217 | 0.2161 | 0.49 | 否 |
| 484 | `SchoolScene/Interior/SportClassProps/SportBasketShit01 (5)/BasketWood` | MetallPainted01Red \| MetallPainted01White \| MetallPainted01White | 8 | 0.1579 | 0.2166 | 3.60 | 否 |
| 485 | `SchoolScene/Interior/Class1-3/ElectroWires02_1` | ElectroWires01 | 0 | 0.2217 | 0.2168 | 0.86 | 否 |
| 486 | `SchoolScene/Interior/Class1-3/Electro08_3` | Electro08 | 0 | 0.2217 | 0.2171 | 1.21 | 否 |
| 487 | `SchoolScene/Interior/Doors/Door07a (7)` | Door07a \| Doorparts01 | 0 | 0.2217 | 0.2179 | 4.56 | 否 |
| 488 | `SchoolScene/Interior/Doors/Door07a (6)` | Door07a \| Doorparts01 | 0 | 0.2217 | 0.2179 | 1.01 | 否 |
| 489 | `SchoolScene/Interior/ClassTeachers/ChairOffice01 (3)/Plastic` | PlasticBlack1 | 11 | 0.2427 | 0.2179 | 3.86 | 否 |
| 490 | `SchoolScene/Interior/Toilets/Toilet01 (6)` | Toilet01 | 11 | 0.2427 | 0.2181 | 4.12 | 否 |
| 491 | `SchoolScene/Interior/Kitchen/KitchenFurniture04_1` | MetallChrome03 \| PlasticBlack1 | 5 | 0.3190 | 0.2182 | 0.47 | 否 |
| 492 | `SchoolScene/Interior/SportClassProps/SportGates02 (2)/Gates002` | SportGates | 3 | 0.2654 | 0.2183 | 0.98 | 否 |
| 493 | `SchoolScene/Interior/Toilets/ToiletCabinet01/ToiletDoor3` | ToiletCabinet01 | 12 | 0.3243 | 0.2186 | 4.72 | 否 |
| 494 | `SchoolScene/Exterior/FenceConcrete1_5 (4)` | ConcreteWall2 | 10 | 0.1624 | 0.2189 | 1.00 | 否 |
| 495 | `SchoolScene/Interior/Toilets/Sink01Soap` | Sink01 | 0 | 0.2217 | 0.2193 | 5.14 | 否 |
| 496 | `SchoolScene/Interior/Toilets/Sink01Soap (1)` | Sink01 | 0 | 0.2217 | 0.2193 | 5.14 | 否 |
| 497 | `SchoolScene/Interior/Kitchen/Door02Wood1/DoorAnchor/door02_1` | DoorWindowsWood1 | 3 | 0.2654 | 0.2194 | 1.01 | 否 |
| 498 | `SchoolScene/Interior/Class1-2/ChemistryProp02 (5)` | GlassClear | 0 | 0.2217 | 0.2200 | 0.99 | 否 |
| 499 | `SchoolScene/Interior/Class1-2/ChemistryProp02 (3)` | GlassClear | 0 | 0.2217 | 0.2200 | 1.73 | 否 |
| 500 | `SchoolScene/Interior/Class1-2/ChemistryProp02 (6)` | GlassClear | 0 | 0.2217 | 0.2200 | 1.73 | 否 |
| 501 | `SchoolScene/Interior/Class1-2/ChemistryProp02 (4)` | GlassClear | 0 | 0.2217 | 0.2200 | 1.73 | 否 |
| 502 | `SchoolScene/Interior/Class1-2/ChemistryProp02 (2)` | GlassClear | 0 | 0.2217 | 0.2200 | 1.30 | 否 |
| 503 | `SchoolScene/Interior/Class1-2/ChemistryProp02 (1)` | GlassClear | 0 | 0.2217 | 0.2200 | 1.30 | 否 |
| 504 | `SchoolScene/Interior/Class1-2/ChemistryProp02` | GlassClear | 0 | 0.2217 | 0.2200 | 0.98 | 否 |
| 505 | `SchoolScene/Exterior/RoadBordure1e (1)` | ConcreteWall5 | 1 | 0.4002 | 0.2201 | -0.40 | 否 |
| 506 | `SchoolScene/Interior/ClassDirector/Capboard01_05 (2)/Capboard05` | WoodOak | 0 | 0.2217 | 0.2203 | 4.60 | 否 |
| 507 | `SchoolScene/Interior/Class1-2/Capboard01_05 (1)/Capboard05` | PlasticWhite1 | 0 | 0.2217 | 0.2203 | 1.04 | 否 |
| 508 | `SchoolScene/Interior/ClassComputer/TableGroup (5)/Chair06 (2)/Chair06Fabric` | Fabric05c | 10 | 0.1624 | 0.2206 | 0.64 | 否 |
| 509 | `SchoolScene/Interior/ClassComputer/Chair06 (1)/Chair06Fabric` | Fabric05c | 10 | 0.1624 | 0.2206 | 0.64 | 否 |
| 510 | `SchoolScene/Interior/ClassComputer/Chair06 (4)/Chair06Fabric` | Fabric05c | 10 | 0.1624 | 0.2206 | 0.64 | 否 |
| 511 | `SchoolScene/Interior/Class2-1/TableSchool01 (12)/TableMetall` | MetallPainted01Gray | 11 | 0.2427 | 0.2207 | 3.93 | 否 |
| 512 | `SchoolScene/Interior/SportClassProps/SportGates01 (1)/Shape01` | MetallPainted01LinesRed | 8 | 0.1579 | 0.2207 | 0.98 | 否 |
| 513 | `SchoolScene/Interior/ClassTeachers/Table_corner1 (1)/office_desk_var2.bmp` | Table_corner1_left | 10 | 0.1624 | 0.2210 | 3.94 | 否 |
| 514 | `SchoolScene/Interior/Kitchen/KitchenFurniture05/TableChrome` | MetallChrome01 | 0 | 0.2217 | 0.2211 | 0.95 | 否 |
| 515 | `SchoolScene/Interior/Kitchen/KitchenWasher01 (1)/TableChrome` | MetallChrome01 | 0 | 0.2217 | 0.2211 | 0.95 | 否 |
| 516 | `SchoolScene/Exterior/Conditioner01 (1)/conditioner` | ModelConditioner01 | 15 | 0.3132 | 0.2222 | 3.00 | **是** |
| 517 | `SchoolScene/Interior/ClassDirector/ChairOffice01/Plastic` | PlasticBlack1 | 0 | 0.2217 | 0.2224 | 3.86 | 否 |
| 518 | `SchoolScene/Exterior/SchoolBuilding/Stairs02` | MetallGray1 | 10 | 0.1624 | 0.2228 | 2.38 | 否 |
| 519 | `SchoolScene/Interior/ClassComputer/Chair06 (6)/Chair06Metal` | PlasticBlack1 | 0 | 0.2217 | 0.2228 | 0.43 | 否 |
| 520 | `SchoolScene/Interior/Class1-3/Electro05_3 (1)` | Electro05 | 0 | 0.2217 | 0.2229 | 0.78 | 否 |
| 521 | `SchoolScene/Interior/Class1-3/Electro05_3` | Electro05 | 0 | 0.2217 | 0.2229 | 0.88 | 否 |
| 522 | `SchoolScene/Interior/ActZal/Curtain02_1` | FabricCurtain02b \| FabricCurtain02a | 0 | 0.2217 | 0.2230 | 5.67 | 否 |
| 523 | `SchoolScene/Interior/ActZal/Curtain02 (1)` | FabricCurtain02b \| FabricCurtain02a | 0 | 0.2217 | 0.2230 | 5.67 | 否 |
| 524 | `SchoolScene/Interior/Class1-4/Blueprints02` | Geography02b \| Geography01b | 9 | 0.3047 | 0.2242 | 0.79 | 否 |
| 525 | `SchoolScene/Interior/Class1-4/Blueprints02 (1)` | Geography01b \| Geography01b | 9 | 0.3047 | 0.2242 | 0.80 | 否 |
| 526 | `SchoolScene/Interior/Doors/Door02Post (2)` | Door02 | 12 | 0.3243 | 0.2245 | 4.58 | 否 |
| 527 | `SchoolScene/Interior/Class1-3/Electro01` | Electro01 | 0 | 0.2217 | 0.2247 | 1.04 | 否 |
| 528 | `SchoolScene/Interior/SportClassProps/SportBasketShit01 (1)/BasketMetal` | MetallPainted01Red \| MetallPainted01Gray | 5 | 0.3190 | 0.2247 | 3.26 | 否 |
| 529 | `SchoolScene/Exterior/BordersArea/RoadBordure1b (12)` | ConcreteWall5 | 10 | 0.1624 | 0.2247 | -0.40 | 否 |
| 530 | `SchoolScene/Interior/Corridor2/Poster04_2a (1)` | Wood3 \| Cork01b \| PosterStickers01 \| ColorChart01 | 9 | 0.3047 | 0.2248 | 5.28 | 否 |
| 531 | `SchoolScene/Interior/ClassDirector/Monitor01 (2)` | Monitor01 | 12 | 0.3243 | 0.2250 | 4.53 | 否 |
| 532 | `SchoolScene/Interior/Class1-3/Electro05_5 (1)` | Electro05 | 0 | 0.2217 | 0.2250 | 0.78 | 否 |
| 533 | `SchoolScene/Interior/Class1-3/Electro05_5` | Electro05 | 0 | 0.2217 | 0.2250 | 0.88 | 否 |
| 534 | `SchoolScene/Interior/ClassDirector/Calculator01` | Chancellery1 | 0 | 0.2217 | 0.2252 | 4.34 | 否 |
| 535 | `SchoolScene/Interior/ClassTeachers/Calculator02` | Chancellery2 | 0 | 0.2217 | 0.2252 | 4.34 | 否 |
| 536 | `SchoolScene/Interior/ClassTeachers/Calculator02 (1)` | Chancellery2 | 0 | 0.2217 | 0.2252 | 4.34 | 否 |
| 537 | `SchoolScene/Interior/ClassComputer/Chair06 (6)/Chair06Fabric` | Fabric05c | 0 | 0.2217 | 0.2254 | 0.64 | 否 |
| 538 | `SchoolScene/Interior/Corridor2/Poster04_1a (4)` | Wood3 \| Cork01b \| PosterStickers01 \| ColorChart01 | 2 | 0.4461 | 0.2255 | 5.29 | 否 |
| 539 | `SchoolScene/Exterior/FenceConcrete1_4` | ConcreteWall2 | 0 | 0.2217 | 0.2257 | 1.00 | 否 |
| 540 | `SchoolScene/Interior/Class1-3/Electro08_10 (2)` | Electro08 | 13 | 0.2761 | 0.2261 | 1.62 | 否 |
| 541 | `SchoolScene/Interior/Class1-3/Electro08_10 (1)` | Electro08 | 13 | 0.2761 | 0.2261 | 0.77 | 否 |
| 542 | `SchoolScene/Exterior/BordersArea (4)/RoadBordure1d` | ConcreteWall5 | 8 | 0.1579 | 0.2267 | -0.40 | 否 |
| 543 | `SchoolScene/Interior/SportClassProps/SportLadder01a (1)` | Wood3 \| MetallGray1 | 5 | 0.3190 | 0.2268 | 1.51 | 否 |
| 544 | `SchoolScene/Interior/ClassDirector/Chair01 (4)/Chair01Fabric` | Fabric05c | 10 | 0.1624 | 0.2268 | 4.26 | 否 |
| 545 | `SchoolScene/Interior/ClassDirector/Chair01 (6)/Chair01Fabric` | Fabric05c | 10 | 0.1624 | 0.2268 | 4.26 | 否 |
| 546 | `SchoolScene/DayLightsInterior/LightLum01_01 (13)` | LightLum01 | 3 | 0.2654 | 0.2269 | 3.28 | 否 |
| 547 | `SchoolScene/DayLightsInterior/LightLum01_01 (16)` | LightLum01 | 3 | 0.2654 | 0.2269 | 3.28 | 否 |
| 548 | `SchoolScene/DayLightsInterior/LightLum01_01 (3)` | LightLum01 | 3 | 0.2654 | 0.2269 | 6.84 | 否 |
| 549 | `SchoolScene/DayLightsInterior/LightLum01_01 (4)` | LightLum01 | 3 | 0.2654 | 0.2269 | 3.28 | 否 |
| 550 | `SchoolScene/DayLightsInterior/LightLum01_01 (13)` | LightLum01 | 3 | 0.2654 | 0.2269 | 6.84 | 否 |
| 551 | `SchoolScene/DayLightsInterior/LightLum01_01 (16)` | LightLum01 | 3 | 0.2654 | 0.2269 | 6.84 | 否 |
| 552 | `SchoolScene/DayLightsInterior/LightLum01_01 (20)` | LightLum01 | 3 | 0.2654 | 0.2269 | 3.28 | 否 |
| 553 | `SchoolScene/DayLightsInterior/LightLum01_01 (11)` | LightLum01 | 3 | 0.2654 | 0.2269 | 3.30 | 否 |
| 554 | `SchoolScene/DayLightsInterior/LightLum01_01 (10)` | LightLum01 | 3 | 0.2654 | 0.2269 | 3.28 | 否 |
| 555 | `SchoolScene/DayLightsInterior/LightLum01_01 (9)` | LightLum01 | 3 | 0.2654 | 0.2269 | 3.28 | 否 |
| 556 | `SchoolScene/DayLightsInterior/LightLum01_01 (14)` | LightLum01 | 3 | 0.2654 | 0.2269 | 3.28 | 否 |
| 557 | `SchoolScene/Exterior/RoadBordure1f (1)` | ConcreteWall5 | 1 | 0.4002 | 0.2270 | -0.40 | 否 |
| 558 | `SchoolScene/Exterior/BordersArea/RoadBordure1b` | ConcreteWall5 | 0 | 0.2217 | 0.2271 | -0.40 | 否 |
| 559 | `SchoolScene/Exterior/BordersArea/RoadBordure1b (8)` | ConcreteWall5 | 0 | 0.2217 | 0.2271 | -0.40 | 否 |
| 560 | `SchoolScene/Exterior/BordersArea/RoadBordure1b (9)` | ConcreteWall5 | 0 | 0.2217 | 0.2271 | -0.40 | 否 |
| 561 | `SchoolScene/Exterior/BordersArea/RoadBordure1b (14)` | ConcreteWall5 | 0 | 0.2217 | 0.2271 | -0.40 | 否 |
| 562 | `SchoolScene/Exterior/BordersArea/RoadBordure1b (23)` | ConcreteWall5 | 0 | 0.2217 | 0.2271 | -0.40 | 否 |
| 563 | `SchoolScene/Exterior/BordersArea/RoadBordure1b (25)` | ConcreteWall5 | 0 | 0.2217 | 0.2271 | -0.40 | 否 |
| 564 | `SchoolScene/Exterior/RoadBordure1b (5)` | ConcreteWall5 | 0 | 0.2217 | 0.2271 | -0.40 | 否 |
| 565 | `SchoolScene/Exterior/RoadBordure1b (3)` | ConcreteWall5 | 0 | 0.2217 | 0.2271 | -0.40 | 否 |
| 566 | `SchoolScene/Exterior/RoadBordure1b (6)` | ConcreteWall5 | 0 | 0.2217 | 0.2271 | -0.40 | 否 |
| 567 | `SchoolScene/Interior/ClassComputer/Blinds01c (7)` | BlindsEmmisive \| Default-Material \| Default-Material | 13 | 0.2761 | 0.2272 | 2.04 | 否 |
| 568 | `SchoolScene/Interior/Kitchen/KitchenTable03 (1)` | MetallChrome03 | 5 | 0.3190 | 0.2272 | 0.59 | 否 |
| 569 | `SchoolScene/Interior/Class1-2/FlowerGroup2 (5)/FlowerRoom3a` | FlowerRoom3 | 0 | 0.2217 | 0.2273 | 1.81 | 否 |
| 570 | `SchoolScene/Interior/Class1-2/FlowerRoom3a (2)` | FlowerRoom3 | 0 | 0.2217 | 0.2273 | 2.27 | 否 |
| 571 | `SchoolScene/Interior/Class1-2/FlowerRoom3a (3)` | FlowerRoom3 | 0 | 0.2217 | 0.2273 | 2.27 | 否 |
| 572 | `SchoolScene/Interior/Class1-3/FlowerGroup (3)/FlowerRoom3a (1)` | FlowerRoom3 | 0 | 0.2217 | 0.2273 | 1.85 | 否 |
| 573 | `SchoolScene/Exterior/RoadBordure1a (3)` | ConcreteWall5 | 8 | 0.1579 | 0.2276 | -0.40 | 否 |
| 574 | `SchoolScene/Exterior/RoadBordure1a (4)` | ConcreteWall5 | 8 | 0.1579 | 0.2276 | -0.40 | 否 |
| 575 | `SchoolScene/Interior/Class1-2/FlowerGroup2 (4)/FlowerRoom4b` | FlowerRoom4 | 0 | 0.2217 | 0.2279 | 1.62 | 否 |
| 576 | `SchoolScene/Interior/Class1-2/FlowerRoom4b (2)` | FlowerRoom4 | 0 | 0.2217 | 0.2279 | 2.04 | 否 |
| 577 | `SchoolScene/Interior/Class1-3/FlowerGroup (3)/FlowerRoom4b (1)` | FlowerRoom4 | 0 | 0.2217 | 0.2279 | 1.38 | 否 |
| 578 | `SchoolScene/Interior/Doors/Door01_2R/Door01_2R/door01_2` | DoorsWindowsPlastic | 16 | 0.3336 | 0.2281 | 1.02 | 否 |
| 579 | `SchoolScene/Interior/Class1-2/ChemistryMol02` | ColorChart01 | 0 | 0.2217 | 0.2284 | 0.87 | 否 |
| 580 | `SchoolScene/Interior/Kitchen/SchoolKitchenSeats (15)/TableSimple01_02/TableMetall` | MetallPainted01Gray | 0 | 0.2217 | 0.2285 | 0.40 | 否 |
| 581 | `SchoolScene/Interior/Class2-1/TableSchool01 (9)/TableMetall` | MetallPainted01Gray | 13 | 0.2761 | 0.2294 | 3.93 | 否 |
| 582 | `SchoolScene/Interior/Class2-1/TableSchool01 (8)/TableMetall` | MetallPainted01Gray | 13 | 0.2761 | 0.2294 | 3.93 | 否 |
| 583 | `SchoolScene/Interior/Class2-1/TableSchool01 (3)/TableMetall` | MetallPainted01Gray | 13 | 0.2761 | 0.2294 | 3.93 | 否 |
| 584 | `SchoolScene/Interior/Class2-2/TableSchool01 (14)/TableMetall` | MetallPainted01Gray | 13 | 0.2761 | 0.2294 | 3.93 | 否 |
| 585 | `SchoolScene/Interior/Class1-3/TableSchool01b (4)/TableMetall` | PlasticBlack1 | 13 | 0.2761 | 0.2294 | 0.37 | 否 |
| 586 | `SchoolScene/Interior/Class1-3/TableSchool01b (5)/TableMetall` | PlasticBlack1 | 13 | 0.2761 | 0.2294 | 0.37 | 否 |
| 587 | `SchoolScene/Interior/Class1-3/TableSchool01b/TableMetall` | PlasticBlack1 | 13 | 0.2761 | 0.2294 | 0.37 | 否 |
| 588 | `SchoolScene/Interior/Class1-3/TableSchool01b (1)/TableMetall` | PlasticBlack1 | 13 | 0.2761 | 0.2294 | 0.37 | 否 |
| 589 | `SchoolScene/Interior/Class1-4/TableSchool01 (13)/TableMetall` | MetallPainted01Gray | 13 | 0.2761 | 0.2294 | 0.37 | 否 |
| 590 | `SchoolScene/Interior/ClassComputer/TableGroup/Chair06 (2)/Chair06Fabric` | Fabric05c | 3 | 0.2654 | 0.2294 | 0.64 | 否 |
| 591 | `SchoolScene/Interior/ClassComputer/TableGroup (3)/Chair06 (2)/Chair06Fabric` | Fabric05c | 3 | 0.2654 | 0.2294 | 0.64 | 否 |
| 592 | `SchoolScene/Exterior/Conditioner01 (2)/conditioner` | ModelConditioner01 | 0 | 0.2217 | 0.2296 | 3.00 | **是** |
| 593 | `SchoolScene/Exterior/Conditioner01 (5)/conditioner` | ModelConditioner01 | 0 | 0.2217 | 0.2296 | 6.22 | **是** |
| 594 | `SchoolScene/Interior/ClassDirector/TableOffice1/ry4kaa.bmp` | TableOffice1ruch | 0 | 0.2217 | 0.2300 | 3.85 | 否 |
| 595 | `SchoolScene/Interior/Class1-2/FlowerGroup2 (4)/FlowerHolder01b` | Wood4 \| MetallChrome01 | 0 | 0.2217 | 0.2302 | 1.87 | 否 |
| 596 | `SchoolScene/Interior/Corridor2/Poster04_6a (1)` | Wood3 \| PosterBulletin01 \| PosterStickers01 \| ColorChart01 | 12 | 0.3243 | 0.2303 | 5.31 | 否 |
| 597 | `SchoolScene/Exterior/ConcreteBox01 (12)` | ConcreteClear03 | 11 | 0.2427 | 0.2303 | -0.18 | 否 |
| 598 | `SchoolScene/Interior/SportClassProps/SportBasketShit01 (2)/BasketMetal` | MetallPainted01Red \| MetallPainted01Gray | 11 | 0.2427 | 0.2308 | 3.26 | 否 |
| 599 | `SchoolScene/Interior/Doors/Door07aPost (8)` | Door07a | 13 | 0.2761 | 0.2308 | 4.58 | 否 |
| 600 | `SchoolScene/Interior/Doors/Door07aPost (3)` | Door07a | 13 | 0.2761 | 0.2308 | 1.02 | 否 |
| 601 | `SchoolScene/Interior/Doors/Door07aPost (5)` | Door07a | 13 | 0.2761 | 0.2308 | 1.02 | 否 |
| 602 | `SchoolScene/Interior/ClassComputer/ProjectorScreen01` | PlasticWhite1 \| PlasticBlack1 | 0 | 0.2217 | 0.2309 | 2.06 | 否 |
| 603 | `SchoolScene/Exterior/Hedge01_02 (2)` | plants01a | 3 | 0.2654 | 0.2311 | 0.52 | 否 |
| 604 | `SchoolScene/Exterior/Hedge01_02 (1)` | plants01a | 3 | 0.2654 | 0.2311 | 0.52 | 否 |
| 605 | `SchoolScene/Exterior/Hedge01_02 (3)` | plants01a | 3 | 0.2654 | 0.2311 | 0.52 | 否 |
| 606 | `SchoolScene/DayLightsExterior/RoadLamp1a (1)/LampGlass` | GlassLamp1 | 0 | 0.2217 | 0.2313 | 9.98 | 否 |
| 607 | `SchoolScene/DayLightsExterior/RoadLamp1a (2)/LampGlass` | GlassLamp1 | 0 | 0.2217 | 0.2313 | 9.98 | 否 |
| 608 | `SchoolScene/DayLightsExterior/RoadLamp1a (3)/LampGlass` | GlassLamp1 | 0 | 0.2217 | 0.2313 | 9.98 | 否 |
| 609 | `SchoolScene/DayLightsExterior/RoadLamp1a (4)/LampGlass` | GlassLamp1 | 0 | 0.2217 | 0.2313 | 9.98 | 否 |
| 610 | `SchoolScene/DayLightsExterior/RoadLamp1a (5)/LampGlass` | GlassLamp1 | 0 | 0.2217 | 0.2313 | 9.98 | 否 |
| 611 | `SchoolScene/DayLightsExterior/RoadLamp1a (6)/LampGlass` | GlassLamp1 | 0 | 0.2217 | 0.2313 | 9.98 | 否 |
| 612 | `SchoolScene/DayLightsExterior/RoadLamp1a (7)/LampGlass` | GlassLamp1 | 0 | 0.2217 | 0.2313 | 9.98 | 否 |
| 613 | `SchoolScene/DayLightsExterior/RoadLamp1a (8)/LampGlass` | GlassLamp1 | 0 | 0.2217 | 0.2313 | 9.98 | 否 |
| 614 | `SchoolScene/DayLightsExterior/RoadLamp1a (9)/LampGlass` | GlassLamp1 | 0 | 0.2217 | 0.2313 | 9.98 | 否 |
| 615 | `SchoolScene/DayLightsExterior/RoadLamp1a (10)/LampGlass` | GlassLamp1 | 0 | 0.2217 | 0.2313 | 9.98 | 否 |
| 616 | `SchoolScene/DayLightsExterior/RoadLamp1a (11)/LampGlass` | GlassLamp1 | 0 | 0.2217 | 0.2313 | 9.98 | 否 |
| 617 | `SchoolScene/DayLightsExterior/RoadLamp1a (12)/LampGlass` | GlassLamp1 | 0 | 0.2217 | 0.2313 | 9.98 | 否 |
| 618 | `SchoolScene/DayLightsExterior/RoadLamp1a (13)/LampGlass` | GlassLamp1 | 0 | 0.2217 | 0.2313 | 9.98 | 否 |
| 619 | `SchoolScene/DayLightsExterior/RoadLamp1a/LampGlass` | GlassLamp1 | 0 | 0.2217 | 0.2313 | 9.98 | 否 |
| 620 | `SchoolScene/DayLightsExterior/RoadLamp1a (14)/LampGlass` | GlassLamp1 | 0 | 0.2217 | 0.2313 | 9.98 | 否 |
| 621 | `SchoolScene/DayLightsExterior/RoadLamp1a (2)/LampPillar` | ConcretePillar1a | 0 | 0.2217 | 0.2314 | 4.00 | 否 |
| 622 | `SchoolScene/DayLightsExterior/RoadLamp1a (13)/LampPillar` | ConcretePillar1a | 0 | 0.2217 | 0.2314 | 4.00 | 否 |
| 623 | `SchoolScene/DayLightsExterior/RoadLamp2Off/lamp01_1` | MetallPlate03 | 3 | 0.2654 | 0.2323 | 1.09 | 否 |
| 624 | `SchoolScene/DayLightsExterior/RoadLamp2Off/lamp01_1` | MetallPlate03 | 3 | 0.2654 | 0.2323 | 1.09 | 否 |
| 625 | `SchoolScene/DayLightsExterior/RoadLamp2Off/lamp01_1` | MetallPlate03 | 3 | 0.2654 | 0.2323 | 1.09 | 否 |
| 626 | `SchoolScene/DayLightsExterior/RoadLamp2Off/lamp01_1` | MetallPlate03 | 3 | 0.2654 | 0.2323 | 1.09 | 否 |
| 627 | `SchoolScene/DayLightsExterior/RoadLamp2Off/lamp01_1` | MetallPlate03 | 3 | 0.2654 | 0.2323 | 1.09 | 否 |
| 628 | `SchoolScene/DayLightsExterior/RoadLamp2Off/lamp01_1` | MetallPlate03 | 3 | 0.2654 | 0.2323 | 1.09 | 否 |
| 629 | `SchoolScene/DayLightsExterior/RoadLamp2Off/lamp01_1` | MetallPlate03 | 3 | 0.2654 | 0.2323 | 1.09 | 否 |
| 630 | `SchoolScene/DayLightsExterior/RoadLamp2Off/lamp01_1` | MetallPlate03 | 3 | 0.2654 | 0.2323 | 1.09 | 否 |
| 631 | `SchoolScene/DayLightsExterior/RoadLamp2Off/lamp01_1` | MetallPlate03 | 3 | 0.2654 | 0.2323 | 1.09 | 否 |
| 632 | `SchoolScene/DayLightsExterior/RoadLamp2Off/lamp01_1` | MetallPlate03 | 3 | 0.2654 | 0.2323 | 1.09 | 否 |
| 633 | `SchoolScene/Exterior/BuildingBig02b (3)` | ConcreteWall6g \| ConcreteClear01 \| ConcreteClear01 \| BuildingWindows01 \| ConcreteDirt1b \| PlasticLines1Gray | 16 | 0.3336 | 0.2327 | 7.83 | 否 |
| 634 | `SchoolScene/Interior/Class1-3/Electro08_1 (1)` | Electro08 | 0 | 0.2217 | 0.2332 | 1.21 | 否 |
| 635 | `SchoolScene/Interior/Class1-3/Electro08_4` | Electro08 | 0 | 0.2217 | 0.2332 | 1.22 | 否 |
| 636 | `SchoolScene/Interior/Class1-3/Electro08_1` | Electro08 | 0 | 0.2217 | 0.2332 | 1.22 | 否 |
| 637 | `SchoolScene/Interior/Class1-3/Electro08_5` | Electro08 | 0 | 0.2217 | 0.2332 | 0.87 | 否 |
| 638 | `SchoolScene/Interior/Class1-2/ChemistryProp01 (3)` | GlassClear | 0 | 0.2217 | 0.2337 | 1.63 | 否 |
| 639 | `SchoolScene/Interior/Class1-2/ChemistryProp01 (2)` | GlassClear | 0 | 0.2217 | 0.2337 | 1.63 | 否 |
| 640 | `SchoolScene/Interior/Class1-2/ChemistryProp01 (1)` | GlassClear | 0 | 0.2217 | 0.2337 | 1.20 | 否 |
| 641 | `SchoolScene/Interior/Class1-2/ChemistryProp01` | GlassClear | 0 | 0.2217 | 0.2337 | 0.89 | 否 |
| 642 | `SchoolScene/Exterior/BuildingBig02b (5)` | ConcreteWall6g \| ConcreteClear01 \| ConcreteClear01 \| BuildingWindows01 \| ConcreteDirt1b \| PlasticLines1Gray | 12 | 0.3243 | 0.2340 | 7.83 | 否 |
| 643 | `SchoolScene/Interior/Toilets/Toilet01Paper` | Toilet01 | 0 | 0.2217 | 0.2341 | 4.40 | 否 |
| 644 | `SchoolScene/Interior/Toilets/Toilet01Paper (1)` | Toilet01 | 0 | 0.2217 | 0.2341 | 4.40 | 否 |
| 645 | `SchoolScene/Interior/Toilets/Toilet01Paper (2)` | Toilet01 | 0 | 0.2217 | 0.2341 | 4.40 | 否 |
| 646 | `SchoolScene/Interior/Toilets/Toilet01Paper (3)` | Toilet01 | 0 | 0.2217 | 0.2341 | 4.40 | 否 |
| 647 | `SchoolScene/Interior/Toilets/Toilet01Paper (4)` | Toilet01 | 0 | 0.2217 | 0.2341 | 4.40 | 否 |
| 648 | `SchoolScene/Interior/Toilets/Toilet01Paper (5)` | Toilet01 | 0 | 0.2217 | 0.2341 | 4.40 | 否 |
| 649 | `SchoolScene/Interior/Toilets/Toilet01Paper (6)` | Toilet01 | 0 | 0.2217 | 0.2341 | 4.40 | 否 |
| 650 | `SchoolScene/Interior/Toilets/Toilet01Paper (7)` | Toilet01 | 0 | 0.2217 | 0.2341 | 4.40 | 否 |
| 651 | `SchoolScene/Interior/Class2-1/Board01_1a` | PlasticWhite1 \| Board01a | 0 | 0.2217 | 0.2341 | 5.04 | 否 |
| 652 | `SchoolScene/Interior/Corridor1/Radiator01 (35)` | Radiator01 | 9 | 0.3047 | 0.2343 | 0.28 | 否 |
| 653 | `SchoolScene/Interior/Corridor1/Radiator01 (39)` | Radiator01 | 9 | 0.3047 | 0.2343 | 0.28 | 否 |
| 654 | `SchoolScene/Interior/Corridor1/Radiator01 (41)` | Radiator01 | 9 | 0.3047 | 0.2343 | 0.28 | 否 |
| 655 | `SchoolScene/Interior/Class2-2/Radiator01 (3)` | Radiator01 | 9 | 0.3047 | 0.2343 | 3.86 | 否 |
| 656 | `SchoolScene/Interior/Class2-2/Radiator01 (4)` | Radiator01 | 9 | 0.3047 | 0.2343 | 3.86 | 否 |
| 657 | `SchoolScene/Interior/Class1-4/Radiator01 (3)` | Radiator01 | 9 | 0.3047 | 0.2343 | 0.39 | 否 |
| 658 | `SchoolScene/Interior/Class1-4/Radiator01 (4)` | Radiator01 | 9 | 0.3047 | 0.2343 | 0.39 | 否 |
| 659 | `SchoolScene/Interior/Corridor2/Radiator01 (36)` | Radiator01 | 9 | 0.3047 | 0.2343 | 3.84 | 否 |
| 660 | `SchoolScene/Interior/Corridor2/Radiator01 (37)` | Radiator01 | 9 | 0.3047 | 0.2343 | 3.84 | 否 |
| 661 | `SchoolScene/Interior/Corridor2/Radiator01 (39)` | Radiator01 | 9 | 0.3047 | 0.2343 | 3.84 | 否 |
| 662 | `SchoolScene/Interior/Corridor2/Radiator01 (44)` | Radiator01 | 9 | 0.3047 | 0.2343 | 3.84 | 否 |
| 663 | `SchoolScene/Interior/Corridor2/Radiator01 (45)` | Radiator01 | 9 | 0.3047 | 0.2343 | 3.84 | 否 |
| 664 | `SchoolScene/Interior/Corridor2/Radiator01 (46)` | Radiator01 | 9 | 0.3047 | 0.2343 | 3.84 | 否 |
| 665 | `SchoolScene/Interior/Corridor2/Radiator01 (47)` | Radiator01 | 9 | 0.3047 | 0.2343 | 3.84 | 否 |
| 666 | `SchoolScene/Interior/Corridor1/Poster03_3a (1)/Glass` | PosterText01b | 8 | 0.1579 | 0.2356 | 1.56 | 否 |
| 667 | `SchoolScene/Interior/Class1-2/FlowerGroup2 (4)/FlowerRoom3b` | FlowerRoom3 | 0 | 0.2217 | 0.2361 | 2.28 | 否 |
| 668 | `SchoolScene/Interior/Class1-2/FlowerRoom3b (2)` | FlowerRoom3 | 0 | 0.2217 | 0.2361 | 2.26 | 否 |
| 669 | `SchoolScene/Interior/Class1-3/FlowerGroup (3)/FlowerRoom3b (1)` | FlowerRoom3 | 0 | 0.2217 | 0.2361 | 2.10 | 否 |
| 670 | `SchoolScene/Interior/Kitchen/SchoolKitchenSeats (2)/Chair05 (6)` | Leather01Black \| MetallPainted01Gray | 0 | 0.2217 | 0.2365 | 0.45 | 否 |
| 671 | `SchoolScene/Interior/Kitchen/SchoolKitchenSeats (2)/Chair05 (7)` | Leather01Black \| MetallPainted01Gray | 0 | 0.2217 | 0.2365 | 0.45 | 否 |
| 672 | `SchoolScene/Interior/Kitchen/SchoolKitchenSeats (1)/Chair05 (6)` | Leather01Black \| MetallPainted01Gray | 0 | 0.2217 | 0.2365 | 0.45 | 否 |
| 673 | `SchoolScene/Interior/Kitchen/SchoolKitchenSeats (1)/Chair05 (7)` | Leather01Black \| MetallPainted01Gray | 0 | 0.2217 | 0.2365 | 0.45 | 否 |
| 674 | `SchoolScene/Interior/Doors/Door02 (1)` | Door02 \| Doorparts04 | 3 | 0.2654 | 0.2368 | 4.57 | 否 |
| 675 | `SchoolScene/Interior/Doors/Door07a (4)` | Door07a \| Doorparts01 | 3 | 0.2654 | 0.2368 | 1.01 | 否 |
| 676 | `SchoolScene/Interior/ActZal/Curtain02 (4)` | FabricCurtain02b \| FabricCurtain02a | 11 | 0.2427 | 0.2369 | 5.67 | 否 |
| 677 | `SchoolScene/Interior/ActZal/Curtain02 (5)` | FabricCurtain02b \| FabricCurtain02a | 11 | 0.2427 | 0.2369 | 5.67 | 否 |
| 678 | `SchoolScene/Interior/ClassDirector/ChairOffice01 (1)/Plastic` | PlasticBlack1 | 9 | 0.3047 | 0.2370 | 3.86 | 否 |
| 679 | `SchoolScene/Interior/ClassDirector/Capboard01_03/CapboardChrome` | MetallChrome01 | 0 | 0.2217 | 0.2371 | 4.56 | 否 |
| 680 | `SchoolScene/Interior/Class1-2/Capboard01_03 (2)/CapboardChrome` | MetallChrome01 | 0 | 0.2217 | 0.2371 | 1.00 | 否 |
| 681 | `SchoolScene/Interior/Class1-2/Capboard01_03 (3)/CapboardChrome` | MetallChrome01 | 0 | 0.2217 | 0.2371 | 1.00 | 否 |
| 682 | `SchoolScene/Interior/Class1-2/Capboard01_03 (1)/CapboardChrome` | MetallChrome01 | 0 | 0.2217 | 0.2371 | 1.00 | 否 |
| 683 | `SchoolScene/Interior/Class1-3/Capboard01_03/CapboardChrome` | MetallChrome01 | 0 | 0.2217 | 0.2371 | 1.00 | 否 |
| 684 | `SchoolScene/Interior/Class1-4/Capboard01_03/CapboardChrome` | MetallChrome01 | 0 | 0.2217 | 0.2371 | 1.00 | 否 |
| 685 | `SchoolScene/Interior/Class1-4/Capboard01_03 (1)/CapboardChrome` | MetallChrome01 | 0 | 0.2217 | 0.2371 | 1.00 | 否 |
| 686 | `SchoolScene/Interior/Class1-3/Board01_1b` | PlasticWhite1 \| Board01b | 2 | 0.4461 | 0.2381 | 1.48 | 否 |
| 687 | `SchoolScene/Exterior/FenceConcrete1_3` | ConcreteWall2 | 11 | 0.2427 | 0.2381 | 1.00 | 否 |
| 688 | `SchoolScene/Interior/ClassDirector/ChairOffice01 (1)/Leather` | Chair_Office2a | 11 | 0.2427 | 0.2382 | 4.25 | 否 |
| 689 | `SchoolScene/Interior/SportClassProps/SportBench01 (2)` | MetallPaintedBlack \| Wood5 | 8 | 0.1579 | 0.2387 | 0.16 | 否 |
| 690 | `SchoolScene/Interior/ActZal/Curtain01_1` | FabricCurtain02b | 16 | 0.3336 | 0.2388 | 5.69 | 否 |
| 691 | `SchoolScene/Interior/Kitchen/KitchenFurniture03 (4)` | MetallChrome03 \| PlasticBlack1 | 5 | 0.3190 | 0.2394 | 0.47 | 否 |
| 692 | `SchoolScene/Interior/ClassComputer/Monitor04 (10)` | Monitor04 | 0 | 0.2217 | 0.2400 | 1.00 | 否 |
| 693 | `SchoolScene/Interior/Doors/Door01_Opened/door01_frame` | DoorsWindowsPlastic | 9 | 0.3047 | 0.2403 | 1.30 | 否 |
| 694 | `SchoolScene/Exterior/RoadBordure1c (5)` | ConcreteWall5 | 11 | 0.2427 | 0.2404 | -0.40 | 否 |
| 695 | `SchoolScene/Exterior/RoadBordure1c` | ConcreteWall5 | 11 | 0.2427 | 0.2404 | -0.40 | 否 |
| 696 | `SchoolScene/Exterior/RoadBordure1c (1)` | ConcreteWall5 | 11 | 0.2427 | 0.2404 | -0.40 | 否 |
| 697 | `SchoolScene/Interior/Class1-3/FlowerGroup2 (2)/FlowerHolder01b` | Wood4 \| MetallChrome01 | 9 | 0.3047 | 0.2405 | 1.87 | 否 |
| 698 | `SchoolScene/Interior/Class1-3/FlowerGroup2 (3)/FlowerHolder01b` | Wood4 \| MetallChrome01 | 9 | 0.3047 | 0.2405 | 1.87 | 否 |
| 699 | `SchoolScene/Interior/Class1-4/FlowerGroup2/FlowerHolder01b` | Wood4 \| MetallChrome01 | 9 | 0.3047 | 0.2405 | 1.87 | 否 |
| 700 | `SchoolScene/Interior/Kitchen/SchoolKitchenSeats (16)/TableSimple01_02/TableMetall` | MetallPainted01Gray | 1 | 0.4002 | 0.2412 | 0.40 | 否 |
| 701 | `SchoolScene/Interior/Kitchen/SchoolKitchenSeats (13)/TableSimple01_02/TableMetall` | MetallPainted01Gray | 1 | 0.4002 | 0.2412 | 0.40 | 否 |
| 702 | `SchoolScene/Interior/Kitchen/KitchenFurniture03_2` | MetallChrome03 \| PlasticBlack1 \| GlassMirrorBlack | 5 | 0.3190 | 0.2419 | 0.47 | 否 |
| 703 | `SchoolScene/Exterior/FenceConcrete1_1 (4)` | ConcreteWall2 | 8 | 0.1579 | 0.2421 | 1.00 | 否 |
| 704 | `SchoolScene/Interior/SportClassProps/SportGates01b/Shape01` | MetallPainted01LinesBlack | 0 | 0.2217 | 0.2429 | 0.98 | 否 |
| 705 | `SchoolScene/Interior/SportClassProps/SportGates02 (3)/Shape01` | MetallPainted01LinesBlack | 0 | 0.2217 | 0.2429 | 0.98 | 否 |
| 706 | `SchoolScene/Exterior/Conditioner01/conditioner` | ModelConditioner01 | 12 | 0.3243 | 0.2430 | 3.00 | **是** |
| 707 | `SchoolScene/Exterior/Conditioner01 (7)/conditioner` | ModelConditioner01 | 12 | 0.3243 | 0.2430 | 6.22 | **是** |
| 708 | `SchoolScene/Interior/Kitchen/KitchenTable03` | MetallChrome03 | 0 | 0.2217 | 0.2432 | 0.59 | 否 |
| 709 | `SchoolScene/Interior/Kitchen/SchoolKitchenSeats (6)/Chair05 (6)` | Leather01Black \| MetallPainted01Gray | 11 | 0.2427 | 0.2433 | 0.45 | 否 |
| 710 | `SchoolScene/Interior/Kitchen/SchoolKitchenSeats (6)/Chair05 (7)` | Leather01Black \| MetallPainted01Gray | 11 | 0.2427 | 0.2433 | 0.45 | 否 |
| 711 | `SchoolScene/Interior/Kitchen/SchoolKitchenSeats (5)/Chair05 (5)` | Leather01Black \| MetallPainted01Gray | 11 | 0.2427 | 0.2433 | 0.45 | 否 |
| 712 | `SchoolScene/Interior/Kitchen/SchoolKitchenSeats (5)/Chair05 (6)` | Leather01Black \| MetallPainted01Gray | 11 | 0.2427 | 0.2433 | 0.45 | 否 |
| 713 | `SchoolScene/Interior/Kitchen/SchoolKitchenSeats (5)/Chair05 (7)` | Leather01Black \| MetallPainted01Gray | 11 | 0.2427 | 0.2433 | 0.45 | 否 |
| 714 | `SchoolScene/Interior/Kitchen/Chair05` | Leather01Black \| MetallPainted01Gray | 11 | 0.2427 | 0.2433 | 0.45 | 否 |
| 715 | `SchoolScene/Exterior/SchoolBuilding/Entry` | PlasticLines1Gray \| ConcreteWall01 \| PlasticLines1Gray \| ConcreteClear05 | 13 | 0.2761 | 0.2439 | 1.54 | 否 |
| 716 | `SchoolScene/Interior/Doors/Door07aPost (6)` | Door07a | 0 | 0.2217 | 0.2441 | 1.02 | 否 |
| 717 | `SchoolScene/Interior/Doors/Door07aPost (7)` | Door07a | 0 | 0.2217 | 0.2441 | 4.57 | 否 |
| 718 | `SchoolScene/Interior/Corridor1/Alarm01` | Alarm01 | 0 | 0.2217 | 0.2447 | 2.80 | 否 |
| 719 | `SchoolScene/Interior/Corridor1/Alarm01 (1)` | Alarm01 | 0 | 0.2217 | 0.2447 | 2.80 | 否 |
| 720 | `SchoolScene/DayLightsExterior/RoadLamp1a (2)/LampMetall` | MetallChrome01 | 0 | 0.2217 | 0.2450 | 9.15 | 否 |
| 721 | `SchoolScene/DayLightsExterior/RoadLamp1a (13)/LampMetall` | MetallChrome01 | 0 | 0.2217 | 0.2450 | 9.15 | 否 |
| 722 | `SchoolScene/Interior/SportRoom1/SportKozel02 (1)/Kozel2Metal` | MetallBlack \| MetallBlack \| MetallGray1 \| Leather06 | 10 | 0.1624 | 0.2452 | 0.43 | 否 |
| 723 | `SchoolScene/Interior/Doors/Door07aPost (2)` | Door07a | 8 | 0.1579 | 0.2452 | 1.02 | 否 |
| 724 | `SchoolScene/Interior/Doors/Door07aPost (9)` | Door07a | 8 | 0.1579 | 0.2452 | 4.57 | 否 |
| 725 | `SchoolScene/Exterior/FenceConcrete1_1 (2)` | ConcreteWall2 | 5 | 0.3190 | 0.2454 | 1.00 | 否 |
| 726 | `SchoolScene/Exterior/FenceConcrete1_1 (1)` | ConcreteWall2 | 5 | 0.3190 | 0.2454 | 1.00 | 否 |
| 727 | `SchoolScene/Exterior/FenceConcrete1_1 (5)` | ConcreteWall2 | 5 | 0.3190 | 0.2454 | 1.00 | 否 |
| 728 | `SchoolScene/Interior/ActZal/Seats01 (2)/ChamferBox002` | Leather01Red | 11 | 0.2427 | 0.2462 | 4.27 | 否 |
| 729 | `SchoolScene/Interior/ActZal/Seats01 (5)/ChamferBox002` | Leather01Red | 11 | 0.2427 | 0.2462 | 4.27 | 否 |
| 730 | `SchoolScene/Interior/ActZal/Seats01 (1)/ChamferBox002` | Leather01Red | 11 | 0.2427 | 0.2462 | 4.27 | 否 |
| 731 | `SchoolScene/Interior/ActZal/Seats01 (6)/ChamferBox002` | Leather01Red | 11 | 0.2427 | 0.2462 | 4.27 | 否 |
| 732 | `SchoolScene/Interior/ClassComputer/Table03 (10)` | Wood2 \| Wood4 | 0 | 0.2217 | 0.2464 | 0.38 | 否 |
| 733 | `SchoolScene/Interior/Class1-4/FlowerGroup (1)/FlowerRoom4a (1)` | FlowerRoom4 | 3 | 0.2654 | 0.2467 | 1.47 | 否 |
| 734 | `SchoolScene/Interior/Class1-4/FlowerRoom4a (2)` | FlowerRoom4 | 3 | 0.2654 | 0.2467 | 2.13 | 否 |
| 735 | `SchoolScene/Interior/ClassComputer/TableGroup (1)/Chair06 (2)/Chair06Metal` | PlasticBlack1 | 11 | 0.2427 | 0.2472 | 0.43 | 否 |
| 736 | `SchoolScene/Interior/ClassComputer/Chair06 (5)/Chair06Metal` | PlasticBlack1 | 11 | 0.2427 | 0.2472 | 0.43 | 否 |
| 737 | `SchoolScene/Interior/Kitchen/KitchenRazdacha01_03a/Kitchen03a` | MetallChrome03 \| PlasticRed1 \| PlasticBlack1 \| MetallChrome03 | 0 | 0.2217 | 0.2475 | 0.91 | 否 |
| 738 | `SchoolScene/Exterior/Benches/Bench01_1 (1)/Wood` | Wood2 | 16 | 0.3336 | 0.2477 | -0.08 | 否 |
| 739 | `SchoolScene/Exterior/Benches/Bench01_1/Wood` | Wood2 | 16 | 0.3336 | 0.2477 | -0.08 | 否 |
| 740 | `SchoolScene/Interior/ClassComputer/Table03 (1)` | Wood2 \| Wood4 | 9 | 0.3047 | 0.2477 | 0.37 | 否 |
| 741 | `SchoolScene/Interior/ClassComputer/Table03 (2)` | Wood2 \| Wood4 | 9 | 0.3047 | 0.2477 | 0.37 | 否 |
| 742 | `SchoolScene/Interior/ClassComputer/Table03 (3)` | Wood2 \| Wood4 | 9 | 0.3047 | 0.2477 | 0.37 | 否 |
| 743 | `SchoolScene/Interior/ClassComputer/TableGroup/Table03 (4)` | Wood2 \| Wood4 | 9 | 0.3047 | 0.2477 | 0.37 | 否 |
| 744 | `SchoolScene/Interior/ClassComputer/TableGroup (2)/Table03 (4)` | Wood2 \| Wood4 | 9 | 0.3047 | 0.2477 | 0.37 | 否 |
| 745 | `SchoolScene/Interior/ClassComputer/TableGroup (3)/Table03 (4)` | Wood2 \| Wood4 | 9 | 0.3047 | 0.2477 | 0.37 | 否 |
| 746 | `SchoolScene/Interior/ClassComputer/TableGroup (7)/Table03 (4)` | Wood2 \| Wood4 | 9 | 0.3047 | 0.2477 | 0.37 | 否 |
| 747 | `SchoolScene/Interior/ClassDirector/Blinds01c (1)` | BlindsEmmisive \| Default-Material \| Default-Material | 15 | 0.3132 | 0.2477 | 5.61 | 否 |
| 748 | `SchoolScene/Interior/ClassDirector/Blinds01c` | BlindsEmmisive \| Default-Material \| Default-Material | 15 | 0.3132 | 0.2477 | 5.61 | 否 |
| 749 | `SchoolScene/Interior/Class1-3/FlowerGroup (2)/FlowerRoom3a (1)` | FlowerRoom3 | 11 | 0.2427 | 0.2484 | 1.85 | 否 |
| 750 | `SchoolScene/Interior/Class1-3/Electro02 (1)` | Electro02 | 0 | 0.2217 | 0.2487 | 0.80 | 否 |
| 751 | `SchoolScene/Interior/Class1-3/Electro02` | Electro02 | 0 | 0.2217 | 0.2487 | 0.91 | 否 |
| 752 | `SchoolScene/Interior/SportClassProps/SportLadder01a` | Wood3 \| MetallGray1 | 3 | 0.2654 | 0.2490 | 1.51 | 否 |
| 753 | `SchoolScene/Interior/SportRoom2/Bath01/OilTank01` | MetallChrome01 | 0 | 0.2217 | 0.2494 | 0.00 | 否 |
| 754 | `SchoolScene/Interior/SportRoom3/Bath01 (1)/OilTank01` | MetallChrome01 | 0 | 0.2217 | 0.2494 | 0.00 | 否 |
| 755 | `SchoolScene/Interior/Kitchen/KitchenTable01` | MetallChrome03 | 12 | 0.3243 | 0.2495 | 0.47 | 否 |
| 756 | `SchoolScene/Interior/Kitchen/KitchenTable01 (3)` | MetallChrome03 | 12 | 0.3243 | 0.2495 | 0.47 | 否 |
| 757 | `SchoolScene/Interior/Corridor2/Poster04_1a (2)` | Wood3 \| Cork01b \| PosterStickers01 \| ColorChart01 | 9 | 0.3047 | 0.2497 | 5.29 | 否 |
| 758 | `SchoolScene/Interior/Corridor2/Poster04_1a (3)` | Wood3 \| Cork01b \| PosterStickers01 \| ColorChart01 | 9 | 0.3047 | 0.2497 | 5.29 | 否 |
| 759 | `SchoolScene/Interior/SportRoom2/SportBench02/Bench2Chrome` | MetallChrome03 | 0 | 0.2217 | 0.2497 | 1.12 | 否 |
| 760 | `SchoolScene/Interior/SportRoom2/SportBench02 (1)/Bench2Chrome` | MetallChrome03 | 0 | 0.2217 | 0.2497 | 1.12 | 否 |
| 761 | `SchoolScene/Interior/SportRoom2/SportBench02 (2)/Bench2Chrome` | MetallChrome03 | 0 | 0.2217 | 0.2497 | 1.12 | 否 |
| 762 | `SchoolScene/Interior/SportRoom2/SportBench02 (3)/Bench2Chrome` | MetallChrome03 | 0 | 0.2217 | 0.2497 | 1.12 | 否 |
| 763 | `SchoolScene/Interior/SportRoom3/SportBench02 (4)/Bench2Chrome` | MetallChrome03 | 0 | 0.2217 | 0.2497 | 1.12 | 否 |
| 764 | `SchoolScene/Interior/SportRoom3/SportBench02 (5)/Bench2Chrome` | MetallChrome03 | 0 | 0.2217 | 0.2497 | 1.12 | 否 |
| 765 | `SchoolScene/Interior/SportRoom3/SportBench02 (7)/Bench2Chrome` | MetallChrome03 | 0 | 0.2217 | 0.2497 | 1.12 | 否 |
| 766 | `SchoolScene/Interior/SportRoom3/SportBench02 (8)/Bench2Chrome` | MetallChrome03 | 0 | 0.2217 | 0.2497 | 1.12 | 否 |
| 767 | `SchoolScene/Exterior/BordersArea/RoadBordure1b (26)` | ConcreteWall5 | 11 | 0.2427 | 0.2499 | -0.40 | 否 |
| 768 | `SchoolScene/Exterior/BordersArea/RoadBordure1b (27)` | ConcreteWall5 | 11 | 0.2427 | 0.2499 | -0.40 | 否 |
| 769 | `SchoolScene/Exterior/RoadBordure1b` | ConcreteWall5 | 11 | 0.2427 | 0.2499 | -0.40 | 否 |
| 770 | `SchoolScene/Exterior/RoadBordure1b (7)` | ConcreteWall5 | 11 | 0.2427 | 0.2499 | -0.40 | 否 |

## 五、三块吊顶专项

| 对象 | 图集 | 图集均值 | UV采样亮度 | UV范围 | 材质 |
|---|---|---|---|---|---|
| `SchoolScene/Exterior/SchoolBuilding/DirectorTop` | 20 | 0.1138 | 0.0851 | [0.004,0.004]-[0.976,0.855] | TileArmstrong01_04 |
| `SchoolScene/Exterior/SchoolBuilding/PCClassTop` | 10 | 0.1624 | 0.1097 | [0.004,0.004]-[0.575,0.985] | TileArmstrong01_04 |
| `SchoolScene/Exterior/SchoolBuilding/ZalTop` | 15 | 0.3132 | 0.0351 | [0.004,0.004]-[0.996,0.429] | TileArmstrong01_04 |

