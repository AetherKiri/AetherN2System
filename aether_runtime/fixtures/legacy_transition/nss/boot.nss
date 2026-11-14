chapter main {
 CreateColor("old", 0, 0, 0, 320, 180, "RED");
 CreateName("stage");
 CreateColor("stage/background", 10, 0, 0, 320, 180, "BLUE");
 CreateColor("stage/actor", 20, 80, 0, 80, 180, "GREEN");
 DrawLegacyTransition("stage/*", 1024, "mask.png", true, true);
 CreateColor("complete", 40, 0, 160, 20, 20, "GREEN");
 WaitKey();
 Delete("*");
 CreateColor("old", 0, 0, 0, 320, 180, "RED");
 CreateColor("new", 10, 0, 0, 320, 180, "BLUE");
 DrawLegacyTransition("new", 1024, "", false, true);
 CreateColor("complete", 40, 0, 160, 20, 20, "GREEN");
 WaitKey();
 Delete("*");
 CreateColor("old", 0, 0, 0, 320, 180, "RED");
 WaitKey();
 CreateName("stage");
 CreateColor("stage/background", 10, 0, 0, 320, 180, "BLUE");
 CreateColor("stage/actor", 20, 80, 0, 80, 180, "GREEN");
 DrawLegacyTransition("stage/*", 1024, "", false, false, true);
 WaitKey();
 Move("stage/actor", 0, 160, 0, null, false);
 WaitAction("__legacy_transition_*", null);
 CreateColor("complete", 40, 0, 160, 20, 20, "GREEN");
 WaitKey();
}
