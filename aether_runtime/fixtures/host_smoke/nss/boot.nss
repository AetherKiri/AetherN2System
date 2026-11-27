chapter main
{
    assert_eq(EnableDirect3DHAL(), false);
    CreateColor("screen", 20000i, 0, 0, 320, 180, "＃0000FF");
    Move("screen", 0, cehter, 0, null, false);
    Move("screen", 0, @, @0, null, false);
    Rotate("screen", 0, @0, @0, @0, 0, false);
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
