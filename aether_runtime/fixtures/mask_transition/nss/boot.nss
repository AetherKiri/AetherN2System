chapter main
{
    CreateColor("background", 0, 0, 0, 320, 180, "BLACK");
    CreateColor("scene", 1, 0, 0, 320, 180, "RED");
    CreateColor("scene/actor", 2, 80, 30, 160, 120, "BLUE");
    CreateTexture("scene/translucent", 3, 80, 90, "half.png");
    DrawTransition("scene", 0, 500, 500, 0, null, "mask.png", 0);
    WaitKey();
    DrawTransition("scene", 300, 500, 1000, 0, null, "mask.png", 300);
    CreateColor("complete", 10, 300, 160, 20, 20, "GREEN");
    WaitKey();
    DrawTransition("scene", 0, 1000, 0, 0, null, "mask.png", 0);
    WaitKey();
    Exit();
}
