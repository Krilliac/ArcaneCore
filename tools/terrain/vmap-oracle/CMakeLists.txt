# ArcaneCore w3-terrain-data: native oracle for vmap queries (not part of upstream vmangos).
include_directories(../../src ../../src/shared ../../src/game ../../src/game/vmap ../../src/game/Maps ../../src/framework)
add_definitions(-DMMAP_GENERATOR)
add_executable(VMapProbe main.cpp ../mmap/src/VMapExtensions.cpp)
mangos_target_flags(VMapProbe)
target_link_libraries(VMapProbe vmap shared framework)
