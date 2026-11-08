var _mx = keyboard_check(vk_right) - keyboard_check(vk_left);
var _my = keyboard_check(vk_down) - keyboard_check(vk_up);
var _cx = x + 24;
var _cy = y + 30;
var _toque = point_distance(_cx, _cy, mouse_x, mouse_y);
if (mouse_check_button(mb_left) && _toque > 20) {
_mx = sign(round((mouse_x - _cx) / 10));
_my = sign(round((mouse_y - _cy) / 10));
}
if (_my != 0) face_dir = (_my > 0) ? 0 : 1;
if (_mx != 0) face_dir = (_mx > 0) ? 3 : 2;
x += _mx * spd;
y += _my * spd;
walk_t = (_mx != 0 || _my != 0) ? walk_t + 0.15 : 0;
x = clamp(x, -16, room_width - 32);
y = clamp(y, -24, room_height - 40);
image_index = face_dir * 4 + (round(walk_t) mod 4);
var _usar = keyboard_check_pressed(vk_space) || (mouse_check_button_released(mb_left) && _toque <= 20);
if (_usar) {
var _ox = [0, 0, -14, 14];
var _oy = [12, -14, 0, 0];
var _gx = round((_cx + _ox[face_dir] - 8) / 16) * 16;
var _gy = round((_cy + _oy[face_dir] - 8) / 16) * 16;
var _p = noone;
with (obj_planta) if (x == _gx && y == _gy) _p = id;
var _tm = layer_tilemap_get_id("Terra");
var _feito = false;
if (_p != noone && _p.fase == 3) {
trigo += 1;
instance_destroy(_p);
_feito = true;
}
if (!_feito && _p == noone && tilemap_get_at_pixel(_tm, _gx, _gy) != 0) {
instance_create_layer(_gx, _gy, "Instances", obj_planta);
_feito = true;
}
if (!_feito && _p == noone) tilemap_set_at_pixel(_tm, 12, _gx, _gy);
}
