/*
 * Runs a fuzz harness over files, without libFuzzer (never shipped): replays a crash or a whole corpus with any
 * compiler and sanitizer.   replay_<harness> FILE|FOLDER...
 */
#include <dirent.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>

int LLVMFuzzerTestOneInput(const uint8_t* data, size_t size);

static int run_file(const char* path)
{
    FILE* f = fopen(path, "rb");
    long n;
    uint8_t* b;
    if (f == NULL) { perror(path); return 1; }
    fseek(f, 0, SEEK_END);
    n = ftell(f);
    fseek(f, 0, SEEK_SET);
    b = (uint8_t*)malloc(n > 0 ? (size_t)n : 1);
    if (b == NULL || (n > 0 && fread(b, 1, (size_t)n, f) != (size_t)n)) { fclose(f); free(b); return 1; }
    fclose(f);
    LLVMFuzzerTestOneInput(b, (size_t)n);
    free(b);
    return 0;
}

int main(int argc, char** argv)
{
    int i, files = 0;
    for (i = 1; i < argc; i++)
    {
        struct stat s;
        if (stat(argv[i], &s) != 0) { perror(argv[i]); return 1; }
        if (S_ISDIR(s.st_mode))
        {
            DIR* d = opendir(argv[i]);
            struct dirent* e;
            char path[4096];
            if (d == NULL) { perror(argv[i]); return 1; }
            while ((e = readdir(d)) != NULL)
            {
                if (e->d_name[0] == '.') continue;
                snprintf(path, sizeof path, "%s/%s", argv[i], e->d_name);
                if (run_file(path) != 0) { closedir(d); return 1; }
                files++;
            }
            closedir(d);
        }
        else
        {
            if (run_file(argv[i]) != 0) return 1;
            files++;
        }
    }
    printf("replayed %d inputs\n", files);
    return 0;
}
