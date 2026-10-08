// Native vmangos VMapManager2 queries, used as an oracle for ArcaneCore's managed vmap reader.
// Usage: VMapProbe <vmapsDir> <mapId> <x> <y> <z> [<x2> <y2> <z2>]
//        VMapProbe <vmapsDir> --batch <queries.txt>
//   batch lines: "map x y z x2 y2 z2"; output lines: "height(z,50) los(ignoreM2) area flags rootId groupId"
#include "VMapManager2.h"
#include "ModelInstance.h"
#include "WorldModel.h"
#include "MapTree.h"
#include <vector>
#include <cstdio>
#include <cstdlib>
#include <cmath>
#include <cstring>
#include <set>
#include <utility>

using namespace VMAP;

char const* g_mainLogFileName = "VMapProbe.log";

static std::set<std::pair<unsigned, int>> s_loaded;

static void LoadAround(VMapManager2& mgr, char const* dir, unsigned map, float x, float y)
{
    // vmangos TerrainInfo::GetGrid: "It's reversed" - grid x comes from world y.
    int gx = (int)(32 - y / 533.33333f), gy = (int)(32 - x / 533.33333f);
    for (int i = gx - 1; i <= gx + 1; ++i)
        for (int j = gy - 1; j <= gy + 1; ++j)
        {
            if (i < 0 || j < 0 || i > 63 || j > 63) continue;
            if (!s_loaded.insert({ map, i * 64 + j }).second) continue;
            mgr.loadMap(dir, map, i, j);
        }
}

int main(int argc, char** argv)
{
    VMapManager2 mgr;
    mgr.setEnableLineOfSightCalc(true);
    mgr.setEnableHeightCalc(true);
    if (argc == 4 && strcmp(argv[2], "--batch") == 0)
    {
        FILE* f = fopen(argv[3], "r");
        if (!f) return 2;
        unsigned map; float x, y, z, x2, y2, z2;
        while (fscanf(f, "%u %f %f %f %f %f %f", &map, &x, &y, &z, &x2, &y2, &z2) == 7)
        {
            LoadAround(mgr, argv[1], map, x, y);
            LoadAround(mgr, argv[1], map, x2, y2);
            float h = mgr.getHeight(map, x, y, z, 50);
            bool los = mgr.isInLineOfSight(map, x, y, z, x2, y2, z2, true);
            float az = z; uint32 flags = 0; int32 adt = 0, root = 0, group = 0;
            bool area = mgr.getAreaInfo(map, x, y, az, flags, adt, root, group);
            printf("%.4f %d %d %u %d %d\n", h, los ? 1 : 0, area ? 1 : 0, area ? flags : 0, area ? root : 0, area ? group : 0);
        }
        fclose(f);
        return 0;
    }
    if (argc < 6) { printf("usage: VMapProbe dir map x y z [x2 y2 z2] | VMapProbe dir --batch file\n"); return 2; }
    char const* dir = argv[1];
    unsigned map = atoi(argv[2]);
    float x = (float)atof(argv[3]), y = (float)atof(argv[4]), z = (float)atof(argv[5]);
    LoadAround(mgr, dir, map, x, y);
    printf("height(z+2, 10) = %f\n", mgr.getHeight(map, x, y, z + 2, 10));
    printf("height(z+2, 50) = %f\n", mgr.getHeight(map, x, y, z + 2, 50));
    float az = z + 1; uint32 flags = 0; int32 adt = 0, root = 0, group = 0;
    bool area = mgr.getAreaInfo(map, x, y, az, flags, adt, root, group);
    printf("areaInfo(z+1) = %d z %f flags 0x%X adt %d root %d group %d\n", area ? 1 : 0, az, flags, adt, root, group);
    if (ModelInstance* hit = mgr.FindCollisionModel(map, x, y, z + 2, x, y, z - 48))
        printf("first model down: %s id %u flags %u pos %f %f %f rot %f %f %f scale %f\n", hit->name.c_str(), hit->ID, hit->flags,
               hit->iPos.x, hit->iPos.y, hit->iPos.z, hit->iRot.x, hit->iRot.y, hit->iRot.z, hit->iScale);
    if (ModelInstance* hit = mgr.FindCollisionModel(map, x, y, z + 2, x, y, z - 48))
    {
        G3D::Vector3 o = mgr.convertPositionToInternalRep(x, y, z + 2);
        G3D::Vector3 p = hit->getRot() * (o - hit->iPos) * hit->getScale();
        G3D::Vector3 d = hit->getRot() * G3D::Vector3(0, 0, -1);
        printf("model point %f %f %f dir %g %g %g\n", p.x, p.y, p.z, d.x, d.y, d.z);
        std::vector<GroupModel> groups;
        hit->getWorldModel()->getGroupModels(groups);
        for (size_t g = 0; g < groups.size(); ++g)
        {
            std::vector<G3D::Vector3> v; std::vector<MeshTriangle> t; WmoLiquid* l = nullptr;
            groups[g].getMeshData(v, t, l);
            for (size_t i = 0; i < t.size(); ++i)
            {
                G3D::Vector3 e1 = v[t[i].idx1] - v[t[i].idx0], e2 = v[t[i].idx2] - v[t[i].idx0];
                G3D::Vector3 pp = d.cross(e2); float a = e1.dot(pp);
                if (fabs(a) < 1e-5f) continue;
                float f = 1.0f / a; G3D::Vector3 s = p - v[t[i].idx0]; float u = f * s.dot(pp);
                if (u < 0 || u > 1) continue;
                G3D::Vector3 q = s.cross(e1); float vv = f * d.dot(q);
                if (vv < 0 || u + vv > 1) continue;
                float tt = f * e2.dot(q);
                if (tt > 0 && tt < 50)
                    printf(" tri g%u #%u t %f -> z %f  v0 %f %f %f v1 %f %f %f v2 %f %f %f\n", (unsigned)g, (unsigned)i, tt, z + 2 - tt,
                        v[t[i].idx0].x, v[t[i].idx0].y, v[t[i].idx0].z, v[t[i].idx1].x, v[t[i].idx1].y, v[t[i].idx1].z, v[t[i].idx2].x, v[t[i].idx2].y, v[t[i].idx2].z);
            }
        }
    }
    {
        InstanceTreeMap trees;
        mgr.getInstanceMapTree(trees);
        if (trees.count(map))
        {
            ModelInstance* models = nullptr; uint32 count = 0;
            trees[map]->getModelInstances(models, count);
            G3D::Vector3 o = mgr.convertPositionToInternalRep(x, y, z + 2);
            G3D::Ray ray(o, -G3D::Vector3::unitZ());
            for (uint32 i = 0; i < count; ++i)
            {
                if (!models[i].getWorldModel()) continue;
                float dist = 50;
                if (models[i].intersectRay(ray, dist, false, false))
                    printf("instance slot %u %s id %u hits at z %f bound %f %f %f .. %f %f %f origin %f %f\n", i, models[i].name.c_str(), models[i].ID, z + 2 - dist,
                           models[i].iBound.low().x, models[i].iBound.low().y, models[i].iBound.low().z,
                           models[i].iBound.high().x, models[i].iBound.high().y, models[i].iBound.high().z, o.x, o.y);
            }
        }
    }
    if (argc >= 9)
    {
        float x2 = (float)atof(argv[6]), y2 = (float)atof(argv[7]), z2 = (float)atof(argv[8]);
        LoadAround(mgr, dir, map, x2, y2);
        printf("los(+2) = %d\n", mgr.isInLineOfSight(map, x, y, z + 2, x2, y2, z2 + 2, true) ? 1 : 0);
    }
    return 0;
}
