/*
 *  This file is part of CounterStrikeSharp.
 *  CounterStrikeSharp is free software: you can redistribute it and/or modify
 *  it under the terms of the GNU General Public License as published by
 *  the Free Software Foundation, either version 3 of the License, or
 *  (at your option) any later version.
 *
 *  CounterStrikeSharp is distributed in the hope that it will be useful,
 *  but WITHOUT ANY WARRANTY; without even the implied warranty of
 *  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 *  GNU General Public License for more details.
 *
 *  You should have received a copy of the GNU General Public License
 *  along with CounterStrikeSharp.  If not, see <https://www.gnu.org/licenses/>. *
 */

#include <unordered_set>
#include <vector>

#include "scripting/autonative.h"
#include "scripting/script_engine.h"
#include "igameevents.h"
#include "core/function.h"

namespace counterstrikesharp {

CREATE_GETTER_FUNCTION(Vector, float, Length, Vector*, obj->Length());
CREATE_GETTER_FUNCTION(Vector, float, Length2D, Vector*, obj->Length2D());
CREATE_GETTER_FUNCTION(Vector, float, LengthSqr, Vector*, obj->LengthSqr());
CREATE_GETTER_FUNCTION(Vector, float, Length2DSqr, Vector*, obj->Length2DSqr());
CREATE_GETTER_FUNCTION(Vector, bool, IsZero, Vector*, obj->IsZero());

CREATE_GETTER_FUNCTION(Vector, float, X, Vector*, obj->x);
CREATE_GETTER_FUNCTION(Vector, float, Y, Vector*, obj->y);
CREATE_GETTER_FUNCTION(Vector, float, Z, Vector*, obj->z);

CREATE_SETTER_FUNCTION(Vector, float, X, Vector*, obj->x = value);
CREATE_SETTER_FUNCTION(Vector, float, Y, Vector*, obj->y = value);
CREATE_SETTER_FUNCTION(Vector, float, Z, Vector*, obj->z = value);

// The *_NEW natives hand out heap objects that nothing in core frees: core's own Vector/QAngle
// wrappers allocate managed-side now (OwnedNativeBlock), so only third-party plugins calling
// NativeAPI.VectorNew & co. directly still use them. They can release with the matching *_FREE
// native. These count what is still live, for css_dump_leaks; they used to be vectors holding
// every pointer ever returned, which grew forever on top of the leak itself.
static size_t live_vectors = 0;
static size_t live_angles = 0;
extern std::unordered_set<IGameEvent*> managed_game_events;

extern size_t GetVirtualFunctionCacheSize();

CON_COMMAND(css_dump_leaks, "dump css leaks")
{
    auto virtualFunctionCount = GetVirtualFunctionCacheSize();
    Msg("===== Dumping leaks =====\n");
    Msg("\tVector: %zu (%zu B)\n", live_vectors, live_vectors * sizeof(Vector));
    Msg("\tAngles: %zu (%zu B)\n", live_angles, live_angles * sizeof(QAngle));
    Msg("\tGameEvents: %i (~B)\n", managed_game_events.size());
    Msg("\tVirtual Functions: %i (%zu B)\n", virtualFunctionCount, virtualFunctionCount * sizeof(ValveFunction));
    Msg("\tTotal size: %zu B\n",
        (live_vectors * sizeof(Vector)) + (live_angles * sizeof(QAngle)) + (virtualFunctionCount * sizeof(ValveFunction)));
    Msg("===== Dumping leaks =====\n");
}

Vector2D* Vector2DNew(ScriptContext& script_context) { return new Vector2D(); }

Vector4D* Vector4DNew(ScriptContext& script_context) { return new Vector4D(); }

matrix3x4_t* Matrix3x4New(ScriptContext& script_context) { return new matrix3x4_t(); }

Quaternion* QuaternionNew(ScriptContext& script_context) { return new Quaternion(); }

Vector* VectorNew(ScriptContext& script_context)
{
    live_vectors++;
    return new Vector();
}

QAngle* AngleNew(ScriptContext& script_context)
{
    live_angles++;
    return new QAngle();
}

void VectorFree(ScriptContext& script_context)
{
    if (auto vec = script_context.GetArgument<Vector*>(0))
    {
        delete vec;
        live_vectors--;
    }
}

void AngleFree(ScriptContext& script_context)
{
    if (auto ang = script_context.GetArgument<QAngle*>(0))
    {
        delete ang;
        live_angles--;
    }
}

void Vector2DFree(ScriptContext& script_context) { delete script_context.GetArgument<Vector2D*>(0); }

void Vector4DFree(ScriptContext& script_context) { delete script_context.GetArgument<Vector4D*>(0); }

void Matrix3x4Free(ScriptContext& script_context) { delete script_context.GetArgument<matrix3x4_t*>(0); }

void QuaternionFree(ScriptContext& script_context) { delete script_context.GetArgument<Quaternion*>(0); }

void NativeVectorAngles(ScriptContext& script_context)
{
    auto vec = script_context.GetArgument<Vector*>(0);
    auto pseudoUpVector = script_context.GetArgument<Vector*>(1);
    auto outAngle = script_context.GetArgument<QAngle*>(2);

    if (!pseudoUpVector)
    {
        VectorAngles(*vec, *outAngle);
    }
    else
    {
        VectorAngles(*vec, *pseudoUpVector, *outAngle);
    }
}

void NativeAngleVectors(ScriptContext& script_context)
{
    auto vec = script_context.GetArgument<QAngle*>(0);
    auto fwd = script_context.GetArgument<Vector*>(1);
    auto right = script_context.GetArgument<Vector*>(2);
    auto up = script_context.GetArgument<Vector*>(3);

    AngleVectors(*vec, fwd, right, up);
}

REGISTER_NATIVES(vector, {
    ScriptEngine::RegisterNativeHandler("VECTOR_NEW", VectorNew);
    ScriptEngine::RegisterNativeHandler("VECTOR2D_NEW", Vector2DNew);
    ScriptEngine::RegisterNativeHandler("VECTOR4D_NEW", Vector4DNew);
    ScriptEngine::RegisterNativeHandler("MATRIX3X4_NEW", Matrix3x4New);
    ScriptEngine::RegisterNativeHandler("QUATERNION_NEW", QuaternionNew);
    ScriptEngine::RegisterNativeHandler("ANGLE_NEW", AngleNew);
    ScriptEngine::RegisterNativeHandler("VECTOR_FREE", VectorFree);
    ScriptEngine::RegisterNativeHandler("ANGLE_FREE", AngleFree);
    ScriptEngine::RegisterNativeHandler("VECTOR2D_FREE", Vector2DFree);
    ScriptEngine::RegisterNativeHandler("VECTOR4D_FREE", Vector4DFree);
    ScriptEngine::RegisterNativeHandler("MATRIX3X4_FREE", Matrix3x4Free);
    ScriptEngine::RegisterNativeHandler("QUATERNION_FREE", QuaternionFree);

    ScriptEngine::RegisterNativeHandler("VECTOR_SET_X", VectorSetX);
    ScriptEngine::RegisterNativeHandler("VECTOR_SET_Y", VectorSetY);
    ScriptEngine::RegisterNativeHandler("VECTOR_SET_Z", VectorSetZ);

    ScriptEngine::RegisterNativeHandler("VECTOR_GET_X", VectorGetX);
    ScriptEngine::RegisterNativeHandler("VECTOR_GET_Y", VectorGetY);
    ScriptEngine::RegisterNativeHandler("VECTOR_GET_Z", VectorGetZ);

    ScriptEngine::RegisterNativeHandler("ANGLE_VECTORS", NativeAngleVectors);
    ScriptEngine::RegisterNativeHandler("VECTOR_ANGLES", NativeVectorAngles);
    ScriptEngine::RegisterNativeHandler("VECTOR_LENGTH", VectorGetLength);
    ScriptEngine::RegisterNativeHandler("VECTOR_LENGTH_2D", VectorGetLength2D);
    ScriptEngine::RegisterNativeHandler("VECTOR_LENGTH_SQR", VectorGetLengthSqr);
    ScriptEngine::RegisterNativeHandler("VECTOR_LENGTH_2D_SQR", VectorGetLength2DSqr);
    ScriptEngine::RegisterNativeHandler("VECTOR_IS_ZERO", VectorGetIsZero);
})
} // namespace counterstrikesharp
