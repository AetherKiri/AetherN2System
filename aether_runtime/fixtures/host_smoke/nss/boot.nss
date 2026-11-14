chapter main
{
    CreateColor("screen", 1, 0, 0, 320, 180, "BLUE");
    WaitKey();
    Delete("screen");
    CreateColor("screen", 1, 0, 0, 320, 180, "RED");
    WaitKey();
    Delete("screen");
    CreateColor("screen", 1, 0, 0, 320, 180, "GREEN");
    WaitKey();
    Delete("screen");
    CreateTexture("image", 1, 0, 0, "assets/white.png");
    WaitKey();
    Exit();
}
